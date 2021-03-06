using PaymentAutomation.Enums;
using PaymentAutomation.Models;
using PaymentAutomation.Services.Payroll;
using RazorLight;

namespace PaymentAutomation.Services;

public interface IPayrollService
{
    Task GenerateReportsForWeekEnding(DateOnly weekEndingDate);
}

internal class PayrollService : IPayrollService
{
    private readonly string temporaryFolder =
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tmp");

    private readonly IReportingApiClient reportingApiClient;
    private readonly IRazorLightEngine razorEngine;
    private readonly IPrintToPdfService pdfService;
    private readonly IRolloverService rolloverService;
    private readonly IReadOnlyList<IPayrollPostProcessor> consolidatedPayrollPostProcessors;
    private readonly IReadOnlyList<IPayrollPostProcessor> agentPayrollPostProcessors;

    public PayrollService(
        IReportingApiClient reportingApiClient,
        IRazorLightEngine razorEngine,
        IPrintToPdfService pdfService,
        IRolloverService rolloverService,
        IReadOnlyList<IPayrollPostProcessor>? consolidatedPayrollPostProcessors = null,
        IReadOnlyList<IPayrollPostProcessor>? agentPayrollPostProcessors = null
    )
    {
        this.reportingApiClient = reportingApiClient;
        this.razorEngine = razorEngine;
        this.pdfService = pdfService;
        this.rolloverService = rolloverService;
        this.consolidatedPayrollPostProcessors = consolidatedPayrollPostProcessors ?? new List<IPayrollPostProcessor>();
        this.agentPayrollPostProcessors = agentPayrollPostProcessors ?? new List<IPayrollPostProcessor>();

        Directory.CreateDirectory(temporaryFolder);
    }

    public async Task GenerateReportsForWeekEnding(DateOnly weekEndingDate)
    {
        var (allBookings, allAdjustments) = await reportingApiClient.GetBookingsAndAdjustmentsForWeekEnding(weekEndingDate);
        allAdjustments = allAdjustments
            .Where(a => a.Type != AdjustmentType.PreviousBalance)
            .ToList();

        var rollovers = await rolloverService.ProcessRollovers(weekEndingDate, allBookings, allAdjustments);  // TODO: Create common interface for booking and adjustment financials? (ILineItem?)

        allAdjustments = allAdjustments.Concat(rollovers
            .Where(r => r.PriorBalance < 0)
            .Select(r => new Adjustment
            {
                Agent = r.Agent,
                Type = AdjustmentType.PreviousBalance,
                Description = "Previous balance",
                WeekEndingDate = weekEndingDate,
                FranchisePayable = r.PriorBalance,
            })
        ).ToList();

        var consolidatedReportFilename = await GenerateConsolidatedPayrollForWeekEnding(weekEndingDate, allBookings, allAdjustments);

        foreach (var postProcessor in consolidatedPayrollPostProcessors)
        {
            postProcessor.Process(consolidatedReportFilename, weekEndingDate, null);
        }

        var bookingsAndAdjustmentsByAgent = await GetBookingsAndAdjustmentsByAgent(allBookings, allAdjustments);
        foreach (var (agent, bookings, adjustments) in bookingsAndAdjustmentsByAgent)
        {
            var agentReportFilename = await GenerateAgentPayrollForWeekEnding(weekEndingDate, agent, bookings, adjustments);

            foreach (var postProcessor in agentPayrollPostProcessors)
            {
                postProcessor.Process(agentReportFilename, weekEndingDate, agent);
            }
        }
    }

    private async Task<string> GenerateConsolidatedPayrollForWeekEnding(
        DateOnly weekEndingDate,
        IReadOnlyCollection<Booking> bookings,
        IReadOnlyCollection<Adjustment> adjustments
    )
    {
        var html = await razorEngine.CompileRenderAsync("payrollAll.cshtml", (weekEndingDate, bookings, adjustments));
        var temporaryHtmlFile = Path.Combine(temporaryFolder, "tmp.html");
        File.WriteAllText(temporaryHtmlFile, html);

        string filenamePrefix = GetFilenamePrefixForWeekEnding(weekEndingDate);
        var temporaryPdfFile = Path.Combine(temporaryFolder, $"{filenamePrefix}.pdf");
        pdfService.PrintToPdf(temporaryHtmlFile, temporaryPdfFile);
        File.Delete(temporaryHtmlFile);

        return temporaryPdfFile;
    }

    private async Task<string> GenerateAgentPayrollForWeekEnding(
        DateOnly weekEndingDate,
        Agent agent,
        IReadOnlyCollection<Booking> bookings,
        IReadOnlyCollection<Adjustment> adjustments
    )
    {
        var html = await razorEngine.CompileRenderAsync("payrollAgent.cshtml", (weekEndingDate, agent, bookings, adjustments));
        var temporaryHtmlFile = Path.Combine(temporaryFolder, "tmp.html");
        File.WriteAllText(temporaryHtmlFile, html);

        var formattedAgentName = agent.FullName.Replace(' ', '-');
        string filenamePrefix = GetFilenamePrefixForWeekEnding(weekEndingDate);
        var filename = Path.Combine(temporaryFolder, $"{filenamePrefix}-{formattedAgentName}.pdf");
        pdfService.PrintToPdf(temporaryHtmlFile, filename);
        File.Delete(temporaryHtmlFile);

        return filename;
    }

    private async Task<IEnumerable<(Agent agent, List<Booking> bookings, List<Adjustment> adjustments)>> GetBookingsAndAdjustmentsByAgent(IReadOnlyCollection<Booking> allBookings, IReadOnlyCollection<Adjustment> allAdjustments) =>
        (await reportingApiClient.GetAgents())
            .Where(a => !a.Settings.IsManager)
            .Select(agent => (
                agent,
                allBookings.Where(b => b.Agent.Id == agent.Id).ToList(),  // TODO: Make equality checking Agents work properly
                allAdjustments.Where(a => a.Agent.Id == agent.Id).ToList()
            ));

    private static string GetFilenamePrefixForWeekEnding(DateOnly weekEndingDate) =>
        $"WeekEnding-{weekEndingDate.Year}.{weekEndingDate.Month}.{weekEndingDate.Day}";
}
