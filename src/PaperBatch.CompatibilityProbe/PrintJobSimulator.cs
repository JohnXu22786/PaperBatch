namespace PaperBatch.CompatibilityProbe;

public sealed record SimulatedPrintStep(string State, string JobId, string Source);

public sealed record PrintSimulationReport(
    bool SimulationOnly,
    ProbeEvidence JobIdEvidence,
    IReadOnlyList<SimulatedPrintStep> Steps);

public static class PrintJobSimulator
{
    public static PrintSimulationReport RunGdiJobIdScenario()
    {
        const string jobId = "SIM-0001";
        var steps = new[]
        {
            new SimulatedPrintStep("started", jobId, "simulation"),
            new SimulatedPrintStep("accepted-by-spooler", jobId, "simulation"),
            new SimulatedPrintStep("completed", jobId, "simulation")
        };

        return new PrintSimulationReport(
            SimulationOnly: true,
            JobIdEvidence: ProbeEvidence.Verified(EvidenceSource.Simulation, "deterministic in-process protocol fixture", jobId),
            Steps: steps);
    }
}
