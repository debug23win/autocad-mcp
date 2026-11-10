namespace CadMcp.Core;

/// <summary>A native benchmark must not share an AutoCAD process with existing drawing work.</summary>
public static class BenchmarkIsolation
{
    public static void RequireEmpty(int openDocuments)
    {
        if (openDocuments != 0) throw new CadFault("BENCHMARK_SESSION_NOT_EMPTY", "Для нативного бенчмарка нужен отдельный процесс AutoCAD без открытых DWG. Откройте CAD MCP в отдельной сессии и закройте её стартовый пустой чертёж. Эта проверка не закрывает, не сохраняет и не изменяет чертежи.");
    }
    public static void RequireFixtureOnly(IEnumerable<string> openDocuments, string fixture)
    {
        var ids = openDocuments.ToArray();
        if (ids.Length != 1 || ids[0] != fixture) throw new CadFault("BENCHMARK_SESSION_CHANGED", "Another drawing was opened or the owned fixture is unavailable. Benchmark stopped before this CAD operation; do not automatically retry it.");
    }
}
