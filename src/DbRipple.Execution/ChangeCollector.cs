using System.Text.Json;
using DbRipple.Contracts;
using DbRipple.Core.Capture;

namespace DbRipple.Execution;

/// <summary>
/// C11 변경 수집기(§4 C11, §7.2 1·2·6단계). DB에 접근하지 않는 순수 변환.
/// <para>결정(테스트로 남김, CollectorTests):</para>
/// <list type="bullet">
/// <item><b>짝짓기(UPDATE)</b>: 테이블 키(PK, 없으면 NOT NULL 유일 키) 값이 deleted·inserted 양쪽에 있으면 그 키로 짝짓는다(BY_PK/BY_UNIQUE).
/// 키가 바뀐 행은 묶음 전체가 한 행이면 SINGLE_ROW, 아니면 바뀌지 않은 다른 NOT NULL 유일 키로 짝짓고(BY_UNIQUE, FULL만), 그것도 안 되면 UNPAIRED(옛 키 before 행 + 새 키 after 행).
/// 키 없는 테이블은 한 행이면 SINGLE_ROW, 아니면 UNPAIRED.</item>
/// <item><b>Q11</b>: 키가 바뀐 짝(SINGLE_ROW·BY_UNIQUE)의 RowChange.key는 새 키(inserted 쪽)다. 옛 키 값은 before에 있다. 재조회·최종 차이의 새 키와 맞춘 것이다.
/// INSERT·DELETE의 pairing은 테이블 키 종류(BY_PK/BY_UNIQUE)이고, 키 없는 테이블이면 한 행일 때 SINGLE_ROW, 아니면 UNPAIRED다.</item>
/// <item><b>값</b>(§6.1): 감사 JSON의 숫자는 원문 그대로, 문자열은 그 값, true/false는 "true"/"false", 객체·배열은 원문, null은 null. 생성 속성(열@srid, 열@type)도 별도 키로 둔다.
/// 바뀐 열은 값 비교로 계산하고 생성 속성은 기본 열로 접는다.</item>
/// <item><b>volatile</b>: IDENTITY(INSERT), ROWVERSION(INSERT·UPDATE), NONDETERMINISTIC_DEFAULT(INSERT, 매니페스트), STATEMENT_FUNCTION(그 문장의 직접 대상 묶음, INSERT·UPDATE),
/// TRIGGER_GETDATE(깊이 2 이상 묶음, INSERT·UPDATE, 활성 사용자 트리거의 비결정 쓰기). UPDATE는 바뀐 열이거나 값이 없을 때만. 열당 1개(이 순서가 우선).
/// derived: 계산 열. INSERT·DELETE는 행 이미지에 있는 계산 열 전부, UPDATE는 바뀐 계산 열.</item>
/// <item><b>최종 after</b>(§7.2 6단계 v1.3): 트리거 문맥 트리(ContextLinks 첫 후보)를 문장 순 → 묶음 전위 순(형제는 captureSeq 순)으로 훑어 그 행을 마지막으로 바꾼 묶음의 after를 재조회와 비교한다.
/// 그 행의 마지막 문장 안 묶음들이 한 사슬(앞 묶음이 뒤 묶음의 조상, 경로의 링크가 모두 후보 1개)이 아니면 순서를 정할 수 없다(형제 순서 observed_only).</item>
/// <item><b>Q14</b>: 순서를 정할 수 없으면 전위 순서상 마지막 행 변경(after가 있는 것)을 대표로 after를 재조회 값으로 바꾸고(REREAD), 어떤 묶음 after와도 값이 맞지 않는 열이 있을 때만
/// RereadDifference(OrderUndetermined = true)를 낸다. 순서를 정할 수 있으면 다른 열이 있을 때만 낸다. 재조회 행이 없거나(그 뒤 삭제) 마지막 행 변경에 after가 없으면(DELETE, UNPAIRED 옛 키, KEYS) 고치지 않는다(최종 차이 잔차로 넘김).</item>
/// </list>
/// </summary>
public sealed class ChangeCollector : IChangeCollector
{
    /// <inheritdoc />
    public CollectResult Collect(RawCapture raw, AnalysisResult analysis, BaselineContext baseline)
    {
        ArgumentNullException.ThrowIfNull(raw);
        ArgumentNullException.ThrowIfNull(baseline);
        var tables = baseline.Catalog.ToCaptureInput().ToDictionary(t => t.ObjectId);
        var manifest = baseline.Manifest.Tables.ToDictionary(t => t.Table, StringComparer.Ordinal);
        var computed = baseline.Catalog.Columns.Where(c => c.IsComputed).GroupBy(c => c.ObjectId)
            .ToDictionary(g => g.Key, g => g.Select(c => c.Name).ToHashSet(StringComparer.Ordinal));
        var statementFunctions = (analysis?.Statements ?? []).ToDictionary(s => s.Index);
        var triggerGetdate = baseline.Triggers.Where(t => !t.IsAuditTrigger && !t.IsDisabled)
            .SelectMany(t => t.NondeterministicWrites).GroupBy(w => w.Table, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(w => w.Column).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);

        // 1단계: 문장 나누기 (이전 경계, 이 경계]
        var ranges = new List<(int Index, long Low, long High)>();
        var prev = raw.Boundary0;
        foreach (var e in raw.Executions.OrderBy(e => e.Result.Index))
        {
            if (e.Boundary is not long b) continue;
            ranges.Add((e.Result.Index, prev, b));
            prev = b;
        }
        var imagesBySeq = raw.Images.GroupBy(i => i.CaptureSeq).ToDictionary(g => g.Key, g => g.ToList());

        var nodes = new List<Node>();
        foreach (var h in raw.Headers.OrderBy(h => h.CaptureSeq))
        {
            var range = ranges.FirstOrDefault(r => h.CaptureSeq > r.Low && h.CaptureSeq <= r.High);
            if (range.Index == 0) continue; // 경계 밖(있으면 안 됨)
            tables.TryGetValue(h.TableObjectId, out var t);
            var display = t?.DisplayName ?? Unquote(h.TableName);
            manifest.TryGetValue(display, out var mt);
            var firing = new Firing
            {
                Id = ContractIds.Firing(h.CaptureSeq),
                CaptureSeq = h.CaptureSeq,
                StatementIndex = range.Index,
                Depth = h.NestLevel,
                Table = display,
                Operation = h.Operation,
                RowCount = h.Operation == ChangeOperation.Delete ? h.DeletedCount : h.InsertedCount,
                ImageMode = h.ImageMode,
                AuditOrder = Order(mt, h.Operation),
            };
            var node = new Node(firing, t);
            var images = imagesBySeq.GetValueOrDefault(h.CaptureSeq) ?? [];
            var ctx = new RowContext(
                t, mt, computed.GetValueOrDefault(h.TableObjectId) ?? [],
                firing.Depth == 1 && statementFunctions.TryGetValue(range.Index, out var st) && st.Target == display && st.Kind == h.Operation ? st.NondeterministicColumns : [],
                firing.Depth >= 2 ? triggerGetdate.GetValueOrDefault(display) : null);
            node.Rows.AddRange(BuildRows(firing, images, ctx));
            nodes.Add(node);
        }

        // 2단계: 괄호
        var links = new List<TriggerContextLink>();
        foreach (var group in nodes.GroupBy(n => n.Firing.StatementIndex))
            links.AddRange(Bracket(group.ToList()));
        var linkByChild = links.ToDictionary(l => l.ChildFiringId);

        // 6단계: 전위 순서 최종 after + 재조회
        var differences = ApplyRereads(nodes, linkByChild, raw.Rereads);

        return new CollectResult
        {
            Firings = nodes.Select(n => n.Firing).ToList(),
            RowChanges = nodes.SelectMany(n => n.Rows.Select(r => r.Change)).ToList(),
            ContextLinks = links.OrderBy(l => SeqOf(l.ChildFiringId)).ToList(),
            RereadDifferences = differences,
            ProcedureEntryDeletes = EntryDeletes(raw, analysis, baseline, tables, manifest, imagesBySeq),
        };
    }

    static IReadOnlyList<ProcedureEntryDelete> EntryDeletes(RawCapture raw, AnalysisResult? analysis, BaselineContext baseline,
        IReadOnlyDictionary<int, TableInfo> tables, IReadOnlyDictionary<string, ManifestTable> manifest,
        IReadOnlyDictionary<long, List<AuditRowImageRow>> imagesBySeq)
    {
        if (analysis is not { IsAccepted: true } || !analysis.Statements.Any(s => s.Plan?.FirstIndependentDelete is not null) ||
            raw.Status != PreviewStatus.Ok || raw.SandboxDirty || raw.ResidualCheckFailed || raw.ResidualCommitted.Count != 0 ||
            raw.Capture.Truncated || !baseline.Manifest.RecordsZeroRowDirectDelete || raw.Capture.ManifestVersion != baseline.Manifest.Version ||
            raw.Executions.Count != analysis.Statements.Count) return [];

        // Work with the entire raw sequence. Filtering by table, depth or positive count first could
        // mistake a later cascade for a direct DELETE when the entry DELETE affected zero rows.
        var headers = raw.Headers.OrderBy(h => h.CaptureSeq).ToArray();
        if (raw.Images.LongCount(i => i.RowJson is not null) != raw.Capture.ImageCount ||
            !headers.All(h => CompleteImages(h, imagesBySeq.GetValueOrDefault(h.CaptureSeq)))) return [];

        var proofs = new List<ProcedureEntryDelete>();
        var previous = raw.Boundary0;
        int next = 0;
        for (int i = 0; i < raw.Executions.Count; i++)
        {
            var execution = raw.Executions[i];
            var result = execution.Result;
            var statement = analysis.Statements[i];
            if (result.Status != StatementStatus.Ok || execution.Boundary is not long high || high < previous ||
                statement.Index != result.Index || statement.Kind != result.Kind) return [];
            int start = next;
            while (next < headers.Length && headers[next].CaptureSeq <= high) next++;
            if (start == next)
            {
                if (result.CaptureSeqRange is not null || high != previous) return [];
            }
            else
            {
                var first = headers[start];
                if (result.CaptureSeqRange is not [var lowSeq, var highSeq] || first.CaptureSeq <= previous ||
                    first.CaptureSeq != lowSeq || headers[next - 1].CaptureSeq != highSeq || highSeq != high) return [];

                if (statement.Kind == ChangeOperation.Exec && statement.Plan?.FirstIndependentDelete is { } entry &&
                    result.Plan?.FirstIndependentDelete == entry && statement.Procedure == result.Procedure &&
                    baseline.Catalog.ProcedureModules.Any(m => m.ObjectId == entry.ModuleObjectId && m.Schema + "." + m.Name == result.Procedure) &&
                    first.Operation == ChangeOperation.Delete && entry.Operation == first.Operation && first.NestLevel == 1 &&
                    first.TableObjectId == entry.TargetObjectId && tables.TryGetValue(entry.TargetObjectId, out var table) &&
                    table.QuotedName == first.TableName && table.DisplayName == entry.Target &&
                    manifest.GetValueOrDefault(entry.Target) is { Covered: true })
                    proofs.Add(new(result.Index, ContractIds.Firing(first.CaptureSeq), entry.TargetObjectId, first.Operation));
            }
            previous = high;
        }
        return next == headers.Length ? proofs : [];
    }

    static bool CompleteImages(AuditHeaderRow header, List<AuditRowImageRow>? images)
    {
        if (header.ImageMode != ImageMode.Full || header.InsertedCount < 0 || header.DeletedCount < 0) return false;
        if (header.Operation == ChangeOperation.Delete && header.InsertedCount != 0 ||
            header.Operation == ChangeOperation.Insert && header.DeletedCount != 0) return false;
        return (images?.LongCount(i => i.ImageKind == ImageKind.Inserted) ?? 0) == header.InsertedCount &&
            (images?.LongCount(i => i.ImageKind == ImageKind.Deleted) ?? 0) == header.DeletedCount;
    }

    // ---------------- 2단계 ----------------

    /// <summary>§7.2 2단계(v1.3 보완 포함). nodes는 한 문장의 묶음(captureSeq 순).</summary>
    internal static IEnumerable<TriggerContextLink> Bracket(IReadOnlyList<Node> nodes)
    {
        var ordered = nodes.OrderBy(n => n.Firing.CaptureSeq).ToList();
        var parent = new int?[ordered.Count];
        for (var i = 0; i < ordered.Count; i++)
        {
            var d = ordered[i].Firing.Depth;
            var j = i - 1;
            while (j >= 0 && ordered[j].Firing.Depth > d) j--;
            for (var k = j + 1; k < i; k++)
                if (ordered[k].Firing.Depth == d + 1 && parent[k] is null) parent[k] = i;
        }

        var result = new List<TriggerContextLink>();
        for (var k = 0; k < ordered.Count; k++)
        {
            var child = ordered[k];
            var d = child.Firing.Depth;
            if (d < 2) continue;
            var candidates = new List<int>();
            var cap = Confidence.Inferred;
            if (parent[k] is int g)
            {
                // 앞 형제(같은 문맥: 사이에 더 얕은 묶음 없음) 중 NONE이 있으면 상한 ambiguous, 바로 앞 형제가 NONE이면 그것도 후보(늦게 기록된 그 형제의 자식일 수 있음)
                var siblings = PreviousSiblings(ordered, g).ToList();
                if (siblings.Count > 0 && ordered[siblings[0]].Firing.AuditOrder == AuditOrder.None && SeqOf(ordered[siblings[0]]) < child.Firing.CaptureSeq)
                    candidates.Add(siblings[0]);
                candidates.Add(g);
                if (ordered[g].Firing.AuditOrder == AuditOrder.None || siblings.Any(s => ordered[s].Firing.AuditOrder == AuditOrder.None)) cap = Confidence.Ambiguous;
            }
            else
            {
                // 미배정: 앞에 있는 깊이 d−1 묶음 F(사이에 깊이 d−1 이하 묶음 없음)가 NONE이면 F 뒤에 기록된 F의 자식 → 후보 {F}, ambiguous
                var f = k - 1;
                while (f >= 0 && ordered[f].Firing.Depth >= d) f--;
                if (f >= 0 && ordered[f].Firing.Depth == d - 1 && ordered[f].Firing.AuditOrder == AuditOrder.None)
                {
                    candidates.Add(f);
                    cap = Confidence.Ambiguous;
                }
            }
            if (candidates.Count > 1) cap = Confidence.Ambiguous;
            result.Add(new TriggerContextLink
            {
                ChildFiringId = child.Firing.Id,
                CandidateParentFiringIds = candidates.Select(c => ordered[c].Firing.Id).ToList(),
                ConfidenceCap = cap,
            });
        }
        return result;
    }

    /// <summary>g 앞의 같은 깊이 형제(가까운 것부터). 더 얕은 묶음을 만나면 멈춘다.</summary>
    static IEnumerable<int> PreviousSiblings(IReadOnlyList<Node> ordered, int g)
    {
        var d = ordered[g].Firing.Depth;
        for (var s = g - 1; s >= 0; s--)
        {
            var ds = ordered[s].Firing.Depth;
            if (ds < d) yield break;
            if (ds == d) yield return s;
        }
    }

    // ---------------- 행 변경 ----------------

    internal sealed record RowContext(TableInfo? Table, ManifestTable? Manifest, IReadOnlySet<string> Computed,
        IReadOnlyList<string> StatementFunctionColumns, IReadOnlySet<string>? TriggerGetdateColumns);

    static IEnumerable<RowState> BuildRows(Firing f, IReadOnlyList<AuditRowImageRow> images, RowContext ctx)
    {
        var key = ctx.Table is null ? null : TableKey.Choose(ctx.Table);
        var dels = images.Where(i => i.ImageKind == ImageKind.Deleted).Select(i => Image.From(i)).ToList();
        var ins = images.Where(i => i.ImageKind == ImageKind.Inserted).Select(i => Image.From(i)).ToList();
        var drafts = new List<Draft>();
        var keyed = key is not null && key.Kind != KeyKind.ROW;
        var keyPairing = key?.Kind == KeyKind.UNIQUE ? Pairing.ByUnique : Pairing.ByPk;
        switch (f.Operation)
        {
            case ChangeOperation.Insert:
                drafts.AddRange(ins.Select(i => new Draft(null, i, keyed ? keyPairing : ins.Count == 1 ? Pairing.SingleRow : Pairing.Unpaired)));
                break;
            case ChangeOperation.Delete:
                drafts.AddRange(dels.Select(d => new Draft(d, null, keyed ? keyPairing : dels.Count == 1 ? Pairing.SingleRow : Pairing.Unpaired)));
                break;
            case ChangeOperation.Update:
                drafts.AddRange(PairUpdate(dels, ins, keyed, keyPairing, ctx.Table));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(f), f.Operation, "행 연산만");
        }

        var states = drafts.Select(d => ToRow(f, d, key, ctx)).ToList();
        states.Sort(RowOrder);
        for (var n = 0; n < states.Count; n++)
            states[n] = states[n] with { Change = states[n].Change with { Id = ContractIds.RowChange(f.CaptureSeq, n + 1) } };
        return states;
    }

    static IEnumerable<Draft> PairUpdate(List<Image> dels, List<Image> ins, bool keyed, Pairing keyPairing, TableInfo? table)
    {
        if (dels.Count == 1 && ins.Count == 1 && (!keyed || dels[0].KeyJson != ins[0].KeyJson))
        {
            yield return new Draft(dels[0], ins[0], Pairing.SingleRow);
            yield break;
        }
        var restD = dels;
        var restI = ins;
        if (keyed)
        {
            var insByKey = ins.Where(i => i.KeyJson is not null).GroupBy(i => i.KeyJson!).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            var used = new HashSet<Image>();
            restD = [];
            foreach (var d in dels)
            {
                if (d.KeyJson is not null && insByKey.TryGetValue(d.KeyJson, out var i) && used.Add(i)) yield return new Draft(d, i, keyPairing);
                else restD.Add(d);
            }
            restI = ins.Where(i => !used.Contains(i)).ToList();
        }
        if (restD.Count == 0 && restI.Count == 0) yield break;

        // 바뀌지 않은 다른 NOT NULL 유일 키(FULL 이미지에서만)
        foreach (var uk in table?.UniqueNotNullKeys ?? [])
        {
            if (restD.Any(d => d.Row is null) || restI.Any(i => i.Row is null) || restD.Count != restI.Count) break;
            var dk = restD.ToDictionary(d => Tuple(d.Row!, uk), StringComparer.Ordinal);
            if (dk.Count != restD.Count) continue;
            var ik = restI.ToDictionary(i => Tuple(i.Row!, uk), StringComparer.Ordinal);
            if (ik.Count != restI.Count || !dk.Keys.All(ik.ContainsKey)) continue;
            foreach (var (k, d) in dk) yield return new Draft(d, ik[k], Pairing.ByUnique);
            yield break;
        }
        foreach (var d in restD) yield return new Draft(d, null, Pairing.Unpaired);
        foreach (var i in restI) yield return new Draft(null, i, Pairing.Unpaired);
    }

    static string Tuple(IReadOnlyDictionary<string, string?> row, IReadOnlyList<string> cols) =>
        string.Join("\u001f", cols.Select(c => row.TryGetValue(c, out var v) ? v ?? "\u0000" : "\u0001"));

    static RowState ToRow(Firing f, Draft d, TableKey? key, RowContext ctx)
    {
        // Q11: 짝지은 행의 키는 inserted(새 키) 쪽
        var keyImage = d.After ?? d.Before!;
        var before = d.Before?.Row;
        var after = d.After?.Row;
        var changed = f.Operation == ChangeOperation.Update && before is not null && after is not null ? Changed(before, after) : [];
        return new RowState(new RowChange
        {
            Id = "",
            FiringId = f.Id,
            Operation = f.Operation,
            Key = KeyOf(keyImage, key),
            Before = before,
            After = after,
            ChangedColumns = changed,
            Volatile = VolatileCells(f, ctx, changed, before ?? after),
            Derived = DerivedColumns(f, ctx, changed, before ?? after),
            Pairing = d.Pairing,
            FinalAfterSource = FinalAfterSource.Audit,
        }, keyImage.KeyJson, d.After is not null ? d.After.KeyJson : null, f);
    }

    static RowKey KeyOf(Image image, TableKey? key)
    {
        if (image.KeyJson is null) return new RowKey { Kind = KeyKind.ROW };
        var values = ParseRow(image.KeyJson);
        var cols = key is not null && key.Kind != KeyKind.ROW ? key.Columns : values.Keys.ToList();
        return new RowKey { Kind = key?.Kind is KeyKind.UNIQUE ? KeyKind.UNIQUE : KeyKind.PK, Columns = cols, Values = cols.Select(c => values.GetValueOrDefault(c)).ToList() };
    }

    static IReadOnlyList<VolatileCell> VolatileCells(Firing f, RowContext ctx, IReadOnlyList<string> changed, IReadOnlyDictionary<string, string?>? values)
    {
        if (f.Operation == ChangeOperation.Delete) return [];
        var result = new List<VolatileCell>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        bool Applies(string c) => f.Operation == ChangeOperation.Insert || values is null || changed.Contains(c);
        void Add(string c, VolatileReason r) { if (Applies(c) && seen.Add(c)) result.Add(new VolatileCell { Column = c, Reason = r }); }
        foreach (var v in ctx.Manifest?.VolatileColumns ?? [])
        {
            switch (v.Reason)
            {
                case "IDENTITY" when f.Operation == ChangeOperation.Insert: Add(v.Column, VolatileReason.Identity); break;
                case "ROWVERSION": Add(v.Column, VolatileReason.Rowversion); break;
                case "NONDETERMINISTIC_DEFAULT" when f.Operation == ChangeOperation.Insert: Add(v.Column, VolatileReason.NondeterministicDefault); break;
            }
        }
        foreach (var c in ctx.StatementFunctionColumns) Add(c, VolatileReason.StatementFunction);
        foreach (var c in ctx.TriggerGetdateColumns ?? (IEnumerable<string>)[]) Add(c, VolatileReason.TriggerGetdate);
        var order = ctx.Table?.Columns.Select((c, i) => (c.Name, i)).ToDictionary(x => x.Name, x => x.i, StringComparer.Ordinal);
        return order is null ? result : result.OrderBy(v => order.GetValueOrDefault(v.Column, int.MaxValue)).ToList();
    }

    static IReadOnlyList<string> DerivedColumns(Firing f, RowContext ctx, IReadOnlyList<string> changed, IReadOnlyDictionary<string, string?>? values)
    {
        if (ctx.Computed.Count == 0) return [];
        var cols = ctx.Table?.Columns.Select(c => c.Name).Where(ctx.Computed.Contains) ?? ctx.Computed;
        return f.Operation == ChangeOperation.Update
            ? cols.Where(changed.Contains).ToList()
            : cols.Where(c => values is null || values.ContainsKey(c)).ToList();
    }

    /// <summary>값 비교로 바뀐 열(생성 속성 열@srid·열@type은 기본 열로 접는다). 순서는 after → before 속성 순.</summary>
    internal static IReadOnlyList<string> Changed(IReadOnlyDictionary<string, string?> before, IReadOnlyDictionary<string, string?> after)
    {
        var result = new List<string>();
        foreach (var k in after.Keys.Concat(before.Keys.Where(k => !after.ContainsKey(k))))
        {
            before.TryGetValue(k, out var b);
            after.TryGetValue(k, out var a);
            if (before.ContainsKey(k) && after.ContainsKey(k) && string.Equals(a, b, StringComparison.Ordinal)) continue;
            var col = BaseColumn(k, after, before);
            if (!result.Contains(col)) result.Add(col);
        }
        return result;
    }

    static string BaseColumn(string property, IReadOnlyDictionary<string, string?> a, IReadOnlyDictionary<string, string?> b)
    {
        foreach (var suffix in new[] { AuditValueExpressions.SridSuffix, AuditValueExpressions.TypeSuffix })
        {
            if (!property.EndsWith(suffix, StringComparison.Ordinal)) continue;
            var baseName = property[..^suffix.Length];
            if (a.ContainsKey(baseName) || b.ContainsKey(baseName)) return baseName;
        }
        return property;
    }

    /// <summary>§6.1: 감사 JSON 한 객체를 열 → 문자열 사전으로(숫자 원문 보존). 규칙은 C12와 같은 Core AuditJson(위험 8-2).</summary>
    public static IReadOnlyDictionary<string, string?> ParseRow(string json) => AuditJson.ParseObject(json);

    static int RowOrder(RowState x, RowState y)
    {
        var c = CompareValues(x.Change.Key.Values, y.Change.Key.Values);
        if (c != 0) return c;
        c = (x.Change.Before is null ? 1 : 0).CompareTo(y.Change.Before is null ? 1 : 0);
        if (c != 0) return c;
        return string.CompareOrdinal(Serialize(x.Change.Before ?? x.Change.After), Serialize(y.Change.Before ?? y.Change.After));
    }

    static string Serialize(IReadOnlyDictionary<string, string?>? row) => row is null ? "" : string.Join("\u001f", row.Select(kv => kv.Key + "=" + kv.Value));

    static int CompareValues(IReadOnlyList<string?> a, IReadOnlyList<string?> b)
    {
        for (var i = 0; i < Math.Min(a.Count, b.Count); i++)
        {
            var (x, y) = (a[i], b[i]);
            int c;
            if (decimal.TryParse(x, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var dx)
                && decimal.TryParse(y, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var dy))
                c = dx.CompareTo(dy);
            else c = string.CompareOrdinal(x, y);
            if (c != 0) return c;
        }
        return a.Count.CompareTo(b.Count);
    }

    // ---------------- 6단계 ----------------

    static List<RereadDifference> ApplyRereads(List<Node> nodes, IReadOnlyDictionary<string, TriggerContextLink> links, IReadOnlyList<RereadRow> rereads)
    {
        var byId = nodes.ToDictionary(n => n.Firing.Id);
        string? ParentOf(Node n) => links.TryGetValue(n.Firing.Id, out var l) && l.CandidateParentFiringIds.Count > 0 ? l.CandidateParentFiringIds[^1] : null;
        bool SingleLink(Node n) => !links.TryGetValue(n.Firing.Id, out var l) || l.CandidateParentFiringIds.Count <= 1;

        // 전위 순서 위치: 문장 순 → 뿌리(부모 없음) captureSeq 순 → 자식 captureSeq 순
        var position = new Dictionary<string, int>();
        foreach (var stmt in nodes.GroupBy(n => n.Firing.StatementIndex).OrderBy(g => g.Key))
        {
            var children = stmt.Where(n => ParentOf(n) is not null).GroupBy(n => ParentOf(n)!).ToDictionary(g => g.Key, g => g.OrderBy(n => n.Firing.CaptureSeq).ToList());
            void Visit(Node n)
            {
                position[n.Firing.Id] = position.Count;
                foreach (var c in children.GetValueOrDefault(n.Firing.Id) ?? []) Visit(c);
            }
            foreach (var root in stmt.Where(n => ParentOf(n) is null || !byId.ContainsKey(ParentOf(n)!)).OrderBy(n => n.Firing.CaptureSeq)) Visit(root);
        }
        bool IsAncestor(Node a, Node b)
        {
            for (var p = ParentOf(b); p is not null && byId.TryGetValue(p, out var pn); p = ParentOf(pn))
                if (pn == a) return true;
            return false;
        }
        bool PathSingle(Node a, Node b)
        {
            for (var n = b; n != a; n = byId[ParentOf(n)!])
                if (!SingleLink(n)) return false;
            return true;
        }

        var rowsByKey = nodes.SelectMany(n => n.Rows.Select((r, i) => (Node: n, Index: i, Row: r)))
            .Where(x => x.Row.AfterKeyJson is not null || x.Row.KeyJson is not null)
            .GroupBy(x => (x.Node.Firing.Table, Key: x.Row.AfterKeyJson ?? x.Row.KeyJson!))
            .ToDictionary(g => g.Key, g => g.ToList());

        var diffs = new List<RereadDifference>();
        foreach (var rr in rereads)
        {
            if (rr.RowJson is null || !rowsByKey.TryGetValue((rr.Table, rr.KeyJson), out var group)) continue;
            var lastStmt = group.Max(x => x.Node.Firing.StatementIndex);
            var inLast = group.Where(x => x.Node.Firing.StatementIndex == lastStmt).OrderBy(x => position[x.Node.Firing.Id]).ToList();
            var determined = true;
            for (var i = 1; i < inLast.Count; i++)
            {
                var (a, b) = (inLast[i - 1].Node, inLast[i].Node);
                if (a == b || !IsAncestor(a, b) || !PathSingle(a, b)) { determined = false; break; }
            }
            var last = inLast[^1];
            var reread = ParseRow(rr.RowJson);
            if (determined)
            {
                if (last.Row.Change.After is not { } after) continue;
                var cols = Changed(after, reread);
                if (cols.Count == 0) continue;
                Replace(last, reread);
                diffs.Add(new RereadDifference { RowChangeId = last.Row.Change.Id, Table = rr.Table, Key = last.Row.Change.Key, Columns = cols });
            }
            else
            {
                var withAfter = group.Where(x => x.Row.Change.After is not null).ToList();
                var rep = inLast.LastOrDefault(x => x.Row.Change.After is not null);
                if (rep.Node is null) continue;
                var unmatched = reread.Keys.Where(c => withAfter.All(x => !x.Row.Change.After!.TryGetValue(c, out var v) || !string.Equals(v, reread[c], StringComparison.Ordinal)))
                    .Select(c => BaseColumn(c, reread, reread)).Distinct().ToList();
                Replace(rep, reread);
                if (unmatched.Count > 0)
                    diffs.Add(new RereadDifference { RowChangeId = rep.Row.Change.Id, Table = rr.Table, Key = rep.Row.Change.Key, Columns = unmatched, OrderUndetermined = true });
            }
        }
        return diffs.OrderBy(d => SeqOf(d.RowChangeId)).ThenBy(d => d.RowChangeId, StringComparer.Ordinal).ToList();
    }

    static void Replace((Node Node, int Index, RowState Row) x, IReadOnlyDictionary<string, string?> reread)
    {
        var rc = x.Row.Change;
        var changed = rc.Operation == ChangeOperation.Update && rc.Before is not null ? Changed(rc.Before, reread) : rc.ChangedColumns;
        var derived = rc.Operation == ChangeOperation.Update
            ? x.Node.Table?.Columns.Where(c => c.IsComputed && changed.Contains(c.Name)).Select(c => c.Name).ToList() ?? rc.Derived
            : rc.Derived;
        x.Node.Rows[x.Index] = x.Row with { Change = rc with { After = reread, FinalAfterSource = FinalAfterSource.Reread, ChangedColumns = changed, Derived = derived } };
    }

    // ---------------- 공통 ----------------

    static AuditOrder Order(ManifestTable? mt, ChangeOperation op)
    {
        if (mt is null) return AuditOrder.None;
        var v = op switch
        {
            ChangeOperation.Insert => mt.AuditOrder.Insert,
            ChangeOperation.Update => mt.AuditOrder.Update,
            ChangeOperation.Delete => mt.AuditOrder.Delete,
            _ => throw new ArgumentOutOfRangeException(nameof(op), op, "행 연산만"),
        };
        return v == CaptureManifest.Last ? AuditOrder.Last : AuditOrder.None;
    }

    /// <summary>"[Sales].[SalesOrderDetail]" → "Sales.SalesOrderDetail"(카탈로그에 없을 때만).</summary>
    static string Unquote(string quoted)
    {
        var parts = new List<string>();
        var i = 0;
        while (i < quoted.Length)
        {
            if (quoted[i] == '[')
            {
                var sb = new System.Text.StringBuilder();
                i++;
                while (i < quoted.Length)
                {
                    if (quoted[i] == ']' && i + 1 < quoted.Length && quoted[i + 1] == ']') { sb.Append(']'); i += 2; continue; }
                    if (quoted[i] == ']') { i++; break; }
                    sb.Append(quoted[i++]);
                }
                parts.Add(sb.ToString());
            }
            else i++;
        }
        return parts.Count > 0 ? string.Join(".", parts) : quoted;
    }

    static long SeqOf(Node n) => n.Firing.CaptureSeq;
    static long SeqOf(string id) => long.TryParse(id.Split('-')[1], out var s) ? s : long.MaxValue;

    internal sealed class Node(Firing firing, TableInfo? table)
    {
        public Firing Firing { get; } = firing;
        public TableInfo? Table { get; } = table;
        public List<RowState> Rows { get; } = [];
    }

    /// <summary>행 변경 + 재조회 대응용 감사 keyJson(KeyJson = 행 키 쪽, AfterKeyJson = after가 있으면 그 키).</summary>
    internal sealed record RowState(RowChange Change, string? KeyJson, string? AfterKeyJson, Firing Firing);

    sealed record Draft(Image? Before, Image? After, Pairing Pairing);

    sealed class Image
    {
        public string? KeyJson { get; private init; }
        public IReadOnlyDictionary<string, string?>? Row { get; private init; }
        public static Image From(AuditRowImageRow i) => new() { KeyJson = i.KeyJson, Row = i.RowJson is null ? null : ParseRow(i.RowJson) };
    }
}
