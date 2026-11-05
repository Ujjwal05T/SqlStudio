using CommunityToolkit.Mvvm.ComponentModel;
using SqlStudio.Services;

namespace SqlStudio.ViewModels;

/// <summary>The "Plan" tab under the editor: one statement's execution plan as a tree, with the costly steps marked.</summary>
public sealed partial class PlanViewModel : ResultPanelViewModel
{
    public PlanViewModel(string xml, string title)
    {
        Title = title;
        Xml = xml;
        try
        {
            Plan = QueryPlan.Parse(xml);
            Statements = Plan.Statements;
            SelectedStatement = Statements[0];
        }
        catch (InvalidOperationException ex)
        {
            ErrorText = ex.Message;
        }
    }

    /// <summary>The plan as the server returned it.</summary>
    public string Xml { get; }

    public QueryPlan? Plan { get; }
    public IReadOnlyList<PlanStatement> Statements { get; } = [];
    public string? ErrorText { get; }
    public bool HasError => ErrorText is not null;
    public bool HasPlan => Plan is not null;
    public bool HasSeveralStatements => Statements.Count > 1;

    /// <summary>The text for a .sqlplan file, which SSMS and Azure Data Studio open.</summary>
    public string FileXml => QueryPlan.ForFile(Xml);

    [ObservableProperty] public partial PlanStatement? SelectedStatement { get; set; }
    [ObservableProperty] public partial ErLayoutResult? Layout { get; set; }
    [ObservableProperty] public partial string Summary { get; set; } = "";
    [ObservableProperty] public partial PlanAdvice? Advice { get; set; }

    partial void OnSelectedStatementChanged(PlanStatement? value)
    {
        if (value is null || Plan is null)
            return;
        Layout = ErLayout.Compute(PlanDiagram.Build(value), PlanDiagram.Legend);
        Advice = PlanAdvisor.Analyze(value);
        var worst = value.MostExpensive;
        Summary = $"{(Plan.IsActual ? "Actual" : "Estimated")} plan · cost {value.TotalCost:0.####} · " +
                  $"{value.Operators.Count():N0} operator{(value.Operators.Count() == 1 ? "" : "s")} · " +
                  $"most expensive: {worst.PhysicalOp} ({PlanDiagram.Share(worst.Percent)})";
    }
}
