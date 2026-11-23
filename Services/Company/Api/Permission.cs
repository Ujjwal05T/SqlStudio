namespace SqlStudio.Services.Company.Api;

[Flags]
public enum Permission { None = 0, Read = 1, Write = 2, Ddl = 4, Execute = 8 }

public static class PermissionNames
{
    // Single source of truth for wire names, in flag order.
    private static readonly (Permission Flag, string Name)[] Map =
    [
        (Permission.Read, "read"),
        (Permission.Write, "write"),
        (Permission.Ddl, "ddl"),
        (Permission.Execute, "execute"),
    ];

    public static IReadOnlyList<string> ToNames(Permission p) =>
        Map.Where(m => p.HasFlag(m.Flag)).Select(m => m.Name).ToList();

    public static Permission Parse(IEnumerable<string>? names)
    {
        var result = Permission.None;
        if (names is null) return result;
        foreach (var name in names)
            foreach (var m in Map)
                if (string.Equals(name, m.Name, StringComparison.OrdinalIgnoreCase))
                    result |= m.Flag;
        return result;
    }

    public static string Label(Permission single) => single switch
    {
        Permission.Read => "Read",
        Permission.Write => "Write",
        Permission.Ddl => "Structure change (DDL)",
        Permission.Execute => "Run procedures (execute)",
        _ => single.ToString(),
    };
}
