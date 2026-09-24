namespace AgentOne.Graph;

/// <summary>One PDSA phase as the graph holds it.</summary>
/// <param name="Expected">What the Plan said would happen. Only a Plan phase carries it.</param>
/// <param name="Verdict">met / partial / unmet. Only a Study phase carries it.</param>
/// <param name="Actual">What Study observed against <paramref name="Expected"/>.</param>
public sealed record PdsaPhaseRow(
    string Kind, string TurnId, string Request, string Note, string Created,
    string Expected = "", string Verdict = "", string Actual = "");

/// <summary>A cycle and its phases, in Plan→Do→Study→Act order.</summary>
public sealed record PdsaCycleRow(long Id, string Title, string Status, string Started, string Verdict, IReadOnlyList<PdsaPhaseRow> Phases)
{
    public PdsaPhaseRow? Phase(string kind) => Phases.FirstOrDefault(p => p.Kind == kind);

    public bool Closed => Status is KnowledgeGraph.ClosedStatus or KnowledgeGraph.AbandonedStatus;

    /// <summary>What the Plan promised, for the Study question. Empty when the cycle opened without one.</summary>
    public string Expected => Phase(KnowledgeGraph.PlanPhase)?.Expected ?? "";

    /// <summary>The phases already recorded, as "plan, do" — what the engine is told the cycle has done.</summary>
    public string Progress => Phases.Count == 0 ? "(nothing yet)" : string.Join(", ", Phases.Select(p => p.Kind));
}

/// <summary>What closing a cycle attached to it: the knowledge it produced, and the knowledge it drew on.</summary>
/// <param name="Taught">Items learned during the cycle's own turns — new knowledge the cycle left behind.</param>
/// <param name="BuiltOn">Items the graph handed to the cycle's turns — knowledge already on the shelf that it used.</param>
public sealed record PdsaClosing(long Cycle, string Verdict, IReadOnlyList<string> Taught, IReadOnlyList<string> BuiltOn)
{
    public int Edges => Taught.Count + BuiltOn.Count;
}

/// <summary>Counts for the status block.</summary>
public readonly record struct PdsaStats(long Cycles, long Open, long Phases, long Met, long Judged, long KnowledgeEdges);

/// <summary>
/// The PDSA side of the workspace graph — Deming's Plan · Do · Study · Act
/// recorded as nodes beside the knowledge it produces.
///
/// It lives in the <em>same</em> embedded database as
/// <see cref="KnowledgeGraph"/> on purpose: the point of recording cycles at
/// all is that a finished one can be wired to the knowledge it taught
/// (<c>TAUGHT</c>) and to the knowledge it stood on (<c>BUILT_ON</c>). Two
/// databases could hold the same rows but never that edge, and the edge is
/// the whole idea — a later session asking "what did we learn building the
/// board API?" walks the cycle, not a text search.
///
/// Schema, beside Knowledge/Turn/Rationale/Path:
/// <code>
///   (:Cycle)-[:HAS_PHASE]->(:Phase)&lt;-[:RAN_IN]-(:Turn)
///   (:Cycle)-[:NEXT_CYCLE]->(:Cycle)      the order they were run in
///   (:Cycle)-[:REINFORCES]->(:Cycle)      this cycle exists because that one fell short
///   (:Cycle)-[:TAUGHT]->(:Knowledge)      closed the loop: what it left behind
///   (:Cycle)-[:BUILT_ON]->(:Knowledge)    what it drew on
/// </code>
///
/// The third step is <b>Study</b>, not Check: its verdict is met / partial /
/// unmet against what Plan said would happen, and an unmet cycle is what makes
/// the next one a <c>REINFORCES</c> cycle rather than an unrelated one.
/// </summary>
public sealed partial class KnowledgeGraph
{
    public const string PlanPhase = "plan";
    public const string DoPhase = "do";
    public const string StudyPhase = "study";
    public const string ActPhase = "act";

    public const string OpenStatus = "open";
    public const string ClosedStatus = "closed";
    public const string AbandonedStatus = "abandoned";

    /// <summary>The order phases are shown in, whatever order they were recorded.</summary>
    public static readonly string[] Phases = [PlanPhase, DoPhase, StudyPhase, ActPhase];

    /// <summary>Phase columns a caller may set. Anything else in the meta dictionary is ignored — the name goes into Cypher.</summary>
    private static readonly string[] PhaseMeta = ["expected", "verdict", "actual"];

    private static readonly string[] PdsaDdl =
    [
        "CREATE NODE TABLE Cycle(id INT64, title STRING, started STRING, closed STRING, status STRING, verdict STRING, PRIMARY KEY(id))",
        "CREATE NODE TABLE Phase(id STRING, cycle INT64, kind STRING, request STRING, note STRING, created STRING, " +
        "expected STRING DEFAULT '', verdict STRING DEFAULT '', actual STRING DEFAULT '', PRIMARY KEY(id))",
        "CREATE REL TABLE HAS_PHASE(FROM Cycle TO Phase)",
        "CREATE REL TABLE RAN_IN(FROM Turn TO Phase)",
        "CREATE REL TABLE NEXT_CYCLE(FROM Cycle TO Cycle)",
        "CREATE REL TABLE REINFORCES(FROM Cycle TO Cycle)",
        "CREATE REL TABLE TAUGHT(FROM Cycle TO Knowledge, how STRING)",
        "CREATE REL TABLE BUILT_ON(FROM Cycle TO Knowledge, how STRING)",
    ];

    private void EnsurePdsaSchema() => Ddl(PdsaDdl);

    // ------------------------------------------------------------ writing

    /// <summary>
    /// Opens a cycle. Any cycle still open is abandoned first — one at a time
    /// per workspace, because a second open cycle would silently swallow the
    /// phases meant for the first. The new cycle follows the last one
    /// (<c>NEXT_CYCLE</c>), and reinforces it when that one fell short.
    /// </summary>
    /// <param name="reinforceOf">A cycle this one exists to put right, or 0.</param>
    public long StartCycle(string title, long reinforceOf = 0)
    {
        lock (_gate)
        {
            var previous = NewestCycleId();
            AbandonOpenCore();

            using var tx = _graph.Begin();
            var id = previous + 1;

            _graph.Execute(
                "CREATE (:Cycle {id: $id, title: $title, started: $started, closed: '', status: $status, verdict: ''})",
                new Dictionary<string, object>
                {
                    ["id"] = id, ["title"] = Clip(title, 120), ["started"] = Stamp(), ["status"] = OpenStatus
                });

            if (previous > 0)
                _graph.Execute("MATCH (a:Cycle {id: $a}), (b:Cycle {id: $b}) CREATE (a)-[:NEXT_CYCLE]->(b)",
                    new Dictionary<string, object> { ["a"] = previous, ["b"] = id });

            if (reinforceOf > 0 && reinforceOf != id)
                _graph.Execute("MATCH (a:Cycle {id: $a}), (b:Cycle {id: $b}) CREATE (a)-[:REINFORCES]->(b)",
                    new Dictionary<string, object> { ["a"] = id, ["b"] = reinforceOf });

            tx.Commit();
            return id;
        }
    }

    /// <summary>
    /// Records one phase of a cycle and links it to the turn that performed it.
    /// A phase recorded twice replaces the first — a cycle that plans, gets
    /// corrected and plans again keeps the latest plan, not both.
    /// </summary>
    /// <param name="meta">expected / verdict / actual; other keys are ignored.</param>
    public void RecordPhase(long cycleId, string kind, string turnId, string request, string note,
        IReadOnlyDictionary<string, string>? meta = null)
    {
        lock (_gate)
        {
            using var tx = _graph.Begin();
            var phaseId = $"c{cycleId}-{kind}";

            _graph.Execute(
                "MERGE (p:Phase {id: $id}) SET p.cycle = $cycle, p.kind = $kind, p.request = $request, p.note = $note, p.created = $created",
                new Dictionary<string, object>
                {
                    ["id"] = phaseId, ["cycle"] = cycleId, ["kind"] = kind,
                    ["request"] = Clip(request, 600), ["note"] = Clip(note, 1200), ["created"] = Stamp()
                });

            // MERGE on the phase makes re-recording idempotent; the edges need
            // the same care, or a corrected plan doubles them.
            if (!HasEdge("MATCH (:Cycle {id: $c})-[:HAS_PHASE]->(:Phase {id: $p}) RETURN 1",
                    new Dictionary<string, object> { ["c"] = cycleId, ["p"] = phaseId }))
                _graph.Execute("MATCH (c:Cycle {id: $c}), (p:Phase {id: $p}) CREATE (c)-[:HAS_PHASE]->(p)",
                    new Dictionary<string, object> { ["c"] = cycleId, ["p"] = phaseId });

            if (turnId.Length > 0
                && !HasEdge("MATCH (:Turn {id: $t})-[:RAN_IN]->(:Phase {id: $p}) RETURN 1",
                    new Dictionary<string, object> { ["t"] = turnId, ["p"] = phaseId }))
                _graph.Execute("MATCH (t:Turn {id: $t}), (p:Phase {id: $p}) CREATE (t)-[:RAN_IN]->(p)",
                    new Dictionary<string, object> { ["t"] = turnId, ["p"] = phaseId });

            if (meta is not null)
                foreach (var (key, value) in meta)
                    if (Array.IndexOf(PhaseMeta, key) >= 0)   // whitelist: the key is interpolated into Cypher
                        _graph.Execute($"MATCH (p:Phase {{id: $p}}) SET p.{key} = $v",
                            new Dictionary<string, object> { ["p"] = phaseId, ["v"] = Clip(value, 2000) });

            _graph.Execute("MATCH (c:Cycle {id: $c}) SET c.status = $s",
                new Dictionary<string, object> { ["c"] = cycleId, ["s"] = kind == ActPhase ? ActPhase : kind });

            tx.Commit();
        }
    }

    /// <summary>
    /// Closes the loop. The cycle is wired to the knowledge its own turns
    /// produced (<c>TAUGHT</c>) and to the knowledge the graph handed those
    /// turns (<c>BUILT_ON</c>), so what the cycle learned and what it stood on
    /// are both reachable from it afterwards.
    ///
    /// Call this only once the turn's distillation has finished: knowledge is
    /// learned off the turn, and closing first would attach the edges of a
    /// cycle whose last lesson is not stored yet.
    /// </summary>
    public PdsaClosing CloseCycle(long cycleId, string verdict, string status = ClosedStatus)
    {
        lock (_gate)
        {
            var taught = Ids(
                "MATCH (:Cycle {id: $c})-[:HAS_PHASE]->(:Phase)<-[:RAN_IN]-(:Turn)-[:LEARNED]->(k:Knowledge) RETURN DISTINCT k.id",
                cycleId);
            var builtOn = Ids(
                "MATCH (:Cycle {id: $c})-[:HAS_PHASE]->(:Phase)<-[:RAN_IN]-(t:Turn)<-[:HELPED]-(k:Knowledge) RETURN DISTINCT k.id",
                cycleId);

            // Knowledge learned inside the cycle is not also "built on" — the
            // cycle taught it; a HELPED edge from a later phase of the same
            // cycle would otherwise put it in both lists.
            builtOn = builtOn.Where(id => !taught.Contains(id)).ToList();

            using var tx = _graph.Begin();

            Link("TAUGHT", cycleId, taught, "cycle " + cycleId);
            Link("BUILT_ON", cycleId, builtOn, "cycle " + cycleId);

            _graph.Execute("MATCH (c:Cycle {id: $c}) SET c.status = $s, c.verdict = $v, c.closed = $at",
                new Dictionary<string, object> { ["c"] = cycleId, ["s"] = status, ["v"] = verdict, ["at"] = Stamp() });

            tx.Commit();
            return new PdsaClosing(cycleId, verdict, taught, builtOn);
        }
    }

    /// <summary>Abandons whatever cycle is open — a plan that was never carried out stays in the record as that.</summary>
    public void AbandonOpenCycle()
    {
        lock (_gate)
        {
            using var tx = _graph.Begin();
            AbandonOpenCore();
            tx.Commit();
        }
    }

    private void AbandonOpenCore() =>
        _graph.Execute(
            $"MATCH (c:Cycle) WHERE c.status <> '{ClosedStatus}' AND c.status <> '{AbandonedStatus}' " +
            "SET c.status = $s, c.closed = $at",
            new Dictionary<string, object> { ["s"] = AbandonedStatus, ["at"] = Stamp() });

    private void Link(string relation, long cycleId, IEnumerable<string> knowledgeIds, string how)
    {
        foreach (var id in knowledgeIds)
        {
            if (HasEdge($"MATCH (:Cycle {{id: $c}})-[:{relation}]->(:Knowledge {{id: $k}}) RETURN 1",
                    new Dictionary<string, object> { ["c"] = cycleId, ["k"] = id }))
                continue;

            _graph.Execute(
                $"MATCH (c:Cycle {{id: $c}}), (k:Knowledge {{id: $k}}) CREATE (c)-[:{relation} {{how: $how}}]->(k)",
                new Dictionary<string, object> { ["c"] = cycleId, ["k"] = id, ["how"] = how });
        }
    }

    // ------------------------------------------------------------ reading

    private const string CycleColumns = "c.id, c.title, c.status, c.started, c.verdict";

    /// <summary>The cycle currently being run, or null. One at a time per workspace.</summary>
    public PdsaCycleRow? OpenCycle()
    {
        lock (_gate)
        {
            var rows = _graph.Query(
                $"MATCH (c:Cycle) WHERE c.status <> '{ClosedStatus}' AND c.status <> '{AbandonedStatus}' " +
                $"RETURN {CycleColumns} ORDER BY c.id DESC LIMIT 1", 5);
            return rows.Count == 0 ? null : WithPhases(rows[0]);
        }
    }

    /// <summary>The last cycle that finished, whatever its verdict — what a new Plan may be reinforcing.</summary>
    public PdsaCycleRow? LastClosedCycle()
    {
        lock (_gate)
        {
            var rows = _graph.Query(
                $"MATCH (c:Cycle) WHERE c.status = '{ClosedStatus}' RETURN {CycleColumns} ORDER BY c.id DESC LIMIT 1", 5);
            return rows.Count == 0 ? null : WithPhases(rows[0]);
        }
    }

    public PdsaCycleRow? Cycle(long id)
    {
        lock (_gate)
        {
            var rows = _graph.Query($"MATCH (c:Cycle {{id: $id}}) RETURN {CycleColumns}", 5,
                new Dictionary<string, object> { ["id"] = id });
            return rows.Count == 0 ? null : WithPhases(rows[0]);
        }
    }

    /// <summary>Cycles newest first, with their phases.</summary>
    public IReadOnlyList<PdsaCycleRow> RecentCycles(int limit = 5)
    {
        lock (_gate)
        {
            return _graph.Query($"MATCH (c:Cycle) RETURN {CycleColumns} ORDER BY c.id DESC LIMIT $limit", 5,
                    new Dictionary<string, object> { ["limit"] = (long)limit })
                .Select(WithPhases).ToList();
        }
    }

    /// <summary>The knowledge a closed cycle is wired to, by relation ("TAUGHT" or "BUILT_ON").</summary>
    public IReadOnlyList<KnowledgeItem> CycleKnowledge(long cycleId, string relation)
    {
        var rel = relation.Equals("BUILT_ON", StringComparison.OrdinalIgnoreCase) ? "BUILT_ON" : "TAUGHT";
        lock (_gate)
        {
            return _graph.Query(
                    $"MATCH (:Cycle {{id: $c}})-[:{rel}]->(k:Knowledge) RETURN DISTINCT {Columns} ORDER BY k.created", 6,
                    new Dictionary<string, object> { ["c"] = cycleId })
                .Select(r => new KnowledgeItem(r[0], r[1], r[2], r[3], r[4], long.TryParse(r[5], out var u) ? u : 0))
                .ToList();
        }
    }

    public PdsaStats CycleStats()
    {
        lock (_gate)
        {
            long Count(string cypher) => long.TryParse(_graph.Query(cypher, 1)[0][0], out var n) ? n : 0;
            return new PdsaStats(
                Count("MATCH (c:Cycle) RETURN count(c)"),
                Count($"MATCH (c:Cycle) WHERE c.status <> '{ClosedStatus}' AND c.status <> '{AbandonedStatus}' RETURN count(c)"),
                Count("MATCH (p:Phase) RETURN count(p)"),
                Count($"MATCH (p:Phase) WHERE p.kind = '{StudyPhase}' AND p.verdict = 'met' RETURN count(p)"),
                Count($"MATCH (p:Phase) WHERE p.kind = '{StudyPhase}' AND p.verdict <> '' RETURN count(p)"),
                Count("MATCH (:Cycle)-[r:TAUGHT]->() RETURN count(r)") +
                Count("MATCH (:Cycle)-[r:BUILT_ON]->() RETURN count(r)"));
        }
    }

    // ------------------------------------------------------------ helpers

    /// <summary>Caller already holds <c>_gate</c>.</summary>
    private PdsaCycleRow WithPhases(string[] row)
    {
        var id = long.TryParse(row[0], out var n) ? n : 0;
        var phases = _graph.Query(
                "MATCH (:Cycle {id: $id})-[:HAS_PHASE]->(p:Phase) " +
                "OPTIONAL MATCH (t:Turn)-[:RAN_IN]->(p) " +
                "RETURN p.kind, t.id, p.request, p.note, p.created, p.expected, p.verdict, p.actual", 8,
                new Dictionary<string, object> { ["id"] = id })
            .Select(r => new PdsaPhaseRow(r[0], r[1], r[2], r[3], r[4], r[5], r[6], r[7]))
            .OrderBy(p => Array.IndexOf(Phases, p.Kind) is var i && i < 0 ? 9 : i)
            .ToList();

        return new PdsaCycleRow(id, row[1], row[2], row[3], row[4], phases);
    }

    private long NewestCycleId()
    {
        var rows = _graph.Query("MATCH (c:Cycle) RETURN max(c.id)", 1);
        return rows.Count > 0 && long.TryParse(rows[0][0], out var max) ? max : 0;
    }

    private bool HasEdge(string cypher, Dictionary<string, object> parameters) =>
        _graph.Query(cypher, 1, parameters).Count > 0;

    private List<string> Ids(string cypher, long cycleId) =>
        _graph.Query(cypher, 1, new Dictionary<string, object> { ["c"] = cycleId }).Select(r => r[0]).ToList();

    private static string Stamp() => DateTime.Now.ToString("yyyy-MM-dd HH:mm");
}
