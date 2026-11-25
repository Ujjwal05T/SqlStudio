using SqlStudio.Models;
using SqlStudio.Services.Company.Api;

namespace SqlStudio.Services.Company;

/// <summary>
/// Collects Connect events and blocked-attempt counts and sends them to the company server in batches.
/// It only ever holds server ids, database names, times and counts: the <see cref="IQueryAuditor"/> interface
/// has no way to hand it SQL text. Failures are swallowed and the events kept for the next flush, so auditing
/// can never get in the way of the user's work.
/// </summary>
/// <remarks>
/// <paramref name="accepts"/> says whether a profile still belongs to the current sign-in. An event from a connection that was
/// already opening when the user signed out is dropped, so it can never be sent under the next person's token.
/// </remarks>
public sealed class CompanyAudit(Func<AuditBatchRequest, CancellationToken, Task> send, TimeProvider clock, Func<ConnectionProfile, bool>? accepts = null) : IQueryAuditor, IDisposable
{
    public const int MaxConnects = 1000;
    public const int MaxEventsPerRequest = 200;
    public const int FlushThreshold = 20;
    public static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(30);

    sealed class Counter(Guid eventId, DateTimeOffset at)
    {
        public Guid EventId = eventId;
        public DateTimeOffset At = at;
        public int Count;
    }

    readonly object _gate = new();
    readonly SemaphoreSlim _flushing = new(1, 1);
    readonly List<AuditEventDto> _connects = [];
    readonly Dictionary<(Guid Server, string? Database), Counter> _blocked = [];
    ITimer? _timer;
    bool _started;

    public int PendingConnects { get { lock (_gate) return _connects.Count; } }
    public int PendingBlocked { get { lock (_gate) return _blocked.Values.Sum(c => c.Count); } }

    public void Connected(ConnectionProfile p, string? database)
    {
        if (accepts?.Invoke(p) == false) return; // before our lock: the session's lock is taken inside
        bool flush;
        lock (_gate)
        {
            _connects.Add(new AuditEventDto(Guid.NewGuid(), clock.GetUtcNow(), AuditKind.Connect, p.Id, database, null));
            if (_connects.Count > MaxConnects) _connects.RemoveRange(0, _connects.Count - MaxConnects);
            flush = ShouldFlushLocked();
        }
        if (flush) FlushInBackground();
    }

    public void Blocked(ConnectionProfile p, string? database)
    {
        if (accepts?.Invoke(p) == false) return;
        bool flush;
        lock (_gate)
        {
            if (!_blocked.TryGetValue((p.Id, database), out var c))
                _blocked[(p.Id, database)] = c = new Counter(Guid.NewGuid(), clock.GetUtcNow());
            c.Count++;
            flush = ShouldFlushLocked();
        }
        if (flush) FlushInBackground();
    }

    bool ShouldFlushLocked() => _started && _connects.Count + _blocked.Count >= FlushThreshold;

    /// <summary>Starts flushing every 30 seconds and when 20 events are queued.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_started) return;
            _started = true;
            _timer = clock.CreateTimer(_ => FlushInBackground(), null, FlushInterval, FlushInterval);
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _started = false;
            _timer?.Dispose();
            _timer = null;
        }
    }

    /// <summary>Drops everything queued (used when the user signs out: the next user must not inherit it).</summary>
    public void Clear()
    {
        lock (_gate) { _connects.Clear(); _blocked.Clear(); }
    }

    /// <summary>Takes everything queued out of the queue, as events ready to send. Used at sign-out: the caller sends them best-effort with the token it captured.</summary>
    public List<AuditEventDto> Drain()
    {
        lock (_gate)
        {
            var events = new List<AuditEventDto>(_connects);
            foreach (var kv in _blocked.Where(kv => kv.Value.Count > 0))
                events.Add(new AuditEventDto(kv.Value.EventId, kv.Value.At, AuditKind.BlockedAttempts, kv.Key.Server, kv.Key.Database, kv.Value.Count));
            _connects.Clear();
            _blocked.Clear();
            return events;
        }
    }

    void FlushInBackground() => _ = Task.Run(async () =>
    {
        try { await FlushAsync(CancellationToken.None).ConfigureAwait(false); } catch { } // auditing never breaks anything
    });

    /// <summary>Sends what is queued, 200 events per request. A failed send keeps the rest for next time. Never throws.</summary>
    public async Task FlushAsync(CancellationToken ct)
    {
        try { await _flushing.WaitAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        try
        {
            // What is in flight: the connect events by reference, and each counter's count at this moment.
            List<AuditEventDto> connects;
            List<((Guid, string?) Key, Counter Counter, int Count)> counters;
            lock (_gate)
            {
                connects = [.. _connects];
                counters = [.. _blocked.Select(kv => (kv.Key, kv.Value, kv.Value.Count)).Where(x => x.Count > 0)];
            }

            var items = new List<(AuditEventDto Event, AuditEventDto? Connect, ((Guid, string?) Key, Counter Counter, int Count)? Counter)>();
            foreach (var c in connects) items.Add((c, c, null));
            foreach (var x in counters)
                items.Add((new AuditEventDto(x.Counter.EventId, x.Counter.At, AuditKind.BlockedAttempts, x.Key.Item1, x.Key.Item2, x.Count), null, x));

            for (var i = 0; i < items.Count; i += MaxEventsPerRequest)
            {
                var chunk = items.Skip(i).Take(MaxEventsPerRequest).ToList();
                try { await send(new AuditBatchRequest(chunk.Select(x => x.Event).ToList()), ct).ConfigureAwait(false); }
                catch (CompanyApiException ex) when (IsRejected(ex)) { } // the server will never take this chunk: drop it and go on
                catch { return; } // keep everything not yet sent
                lock (_gate)
                {
                    foreach (var item in chunk)
                    {
                        if (item.Connect is not null) _connects.Remove(item.Connect);
                        else if (item.Counter is { } x)
                        {
                            x.Counter.Count -= x.Count;
                            if (x.Counter.Count <= 0) _blocked.Remove(x.Key);
                            else { x.Counter.EventId = Guid.NewGuid(); x.Counter.At = clock.GetUtcNow(); } // the rest is a new report
                        }
                    }
                }
            }
        }
        finally { _flushing.Release(); }
    }

    /// <summary>A 4xx other than 401 (token) and 429 (slow down) means the request itself is refused, however often it is sent.</summary>
    static bool IsRejected(CompanyApiException ex) => ex.StatusCode is >= 400 and < 500 and not 401 and not 429;

    public void Dispose()
    {
        Stop(); // the semaphore is left alone: a flush may still be running
    }
}
