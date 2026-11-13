using System.Text.RegularExpressions;
using DbRipple.Contracts;
using DbRipple.Core.Catalog;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace DbRipple.Analysis;

/// <summary>
/// C6 배치 분석기(§4 C6, D5). ScriptDom(TSql170Parser, SP4)으로 파싱·배치 분리·허용 목록 판정·위치 계산을 한다.
/// <list type="bullet">
/// <item>GO 분리는 ScriptDom 배치 구분(주석·문자열 안 GO 무시, 소문자·뒤 주석 GO 인식). `GO 2`는 REJECT_GO_COUNT. 줄 중간의 GO는 별칭으로 파싱된다(SP4).</item>
/// <item>배치 번호는 문장이 있는 배치만 1부터 센다(빈 배치는 건너뜀). 문장 번호는 스크립트 전체 기준 1부터, 위치(position)는 배치 안 1부터.</item>
/// <item>문장 원문(Sql)은 스크립트에서 StartOffset·FragmentLength로 잘라낸다(끝 세미콜론은 사용자가 쓴 경우만 포함, 앞 주석·공백은 제외, WITH 절 포함).</item>
/// <item>위치는 스크립트 전체 기준 1부터의 줄·열이고 끝 열은 마지막 문자를 포함한다.</item>
/// <item>스키마를 생략한 이름은 실행 사용자의 기본 스키마 dbo로 해석한다(Q4).</item>
/// <item>빈 스크립트·주석뿐인 스크립트는 거부 + REJECT_EMPTY_SCRIPT 진단 1개(Q5).</item>
/// <item>파싱 오류가 있으면 오류마다 진단을 내고, 오류 구간과 겹치지 않는 문장만 나머지 규칙으로 분석한다.</item>
/// <item>DML 문장은 다른 이유로 거부되더라도 대상 이름이 정해지면 Statements에 담는다(편집기 표시용). 실행은 IsAccepted가 한다.</item>
/// <item>거부 진단은 (문장, 규칙)당 1개이고 첫 발생 위치를 쓴다. 예외는 던지지 않고 REJECT_ANALYSIS_ERROR로 돌려준다.</item>
/// </list>
/// </summary>
public sealed class BatchAnalyzer : IBatchAnalyzer
{
    /// <summary>스키마 생략 이름의 기본 스키마(Q4, Q12).</summary>
    public const string DefaultSchema = "dbo";

    static readonly Regex GoWithCount = new(@"^\s*GO\s+\S", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <inheritdoc />
    public AnalysisResult Analyze(string sql, BaselineContext baseline)
    {
        sql ??= "";
        try
        {
            return new Run(sql, baseline).Execute();
        }
        catch (Exception ex)
        {
            var lines = new LineIndex(sql);
            return new AnalysisResult
            {
                Rejects =
                [
                    new Diagnostic
                    {
                        StatementIndex = null,
                        Span = lines.Span(0, Math.Max(1, sql.Length)),
                        Severity = Severity.Error,
                        RuleId = AnalysisRules.RejectAnalysisError,
                        Message = $"분석 중 내부 오류가 나서 닫힌 쪽으로 거부합니다: {ex.GetType().Name}",
                        DocRef = "D5",
                    },
                ],
            };
        }
    }

    sealed class Run(string sql, BaselineContext baseline)
    {
        readonly LineIndex lines = new(sql);
        readonly TableResolver resolver = new(baseline.Catalog);
        ProcedureAnalyzer? procedures;
        ProcedureAnalyzer Procedures => procedures ??= new ProcedureAnalyzer(baseline.Catalog, resolver);
        readonly List<Diagnostic> rejects = [];
        readonly List<Diagnostic> warnings = [];
        readonly List<AnalyzedStatement> statements = [];

        public AnalysisResult Execute()
        {
            var fragment = SqlSyntax.Parse(sql, out var errors);
            foreach (var e in errors) rejects.Add(ParseErrorDiagnostic(e));

            var script = fragment as TSqlScript;
            var index = 0;
            var batchNo = 0;
            if (script != null)
            {
                foreach (var batch in script.Batches)
                {
                    var stmts = batch.Statements.Where(s => !OverlapsError(s, errors)).ToList();
                    if (stmts.Count == 0) continue;
                    batchNo++;
                    var position = 0;
                    foreach (var s in stmts)
                    {
                        index++;
                        position++;
                        AnalyzeStatement(s, index, batchNo, position);
                    }
                }
            }
            if (errors.Count == 0 && index == 0) rejects.Add(EmptyDiagnostic());

            return new AnalysisResult
            {
                Statements = statements,
                Rejects = Order(rejects),
                Warnings = Order(warnings),
            };
        }

        static List<Diagnostic> Order(List<Diagnostic> list) =>
            list.OrderBy(d => d.Span.StartLine).ThenBy(d => d.Span.StartColumn).ThenBy(d => d.RuleId, StringComparer.Ordinal).ToList();

        // ---- 스크립트 수준 ----

        Diagnostic ParseErrorDiagnostic(ParseError e)
        {
            var isGoCount = GoWithCount.IsMatch(lines.LineText(e.Offset));
            return new Diagnostic
            {
                StatementIndex = null,
                Span = lines.TokenSpanAt(e.Offset),
                Severity = Severity.Error,
                RuleId = isGoCount ? AnalysisRules.RejectGoCount : AnalysisRules.RejectParseError,
                Message = isGoCount
                    ? "GO에 반복 횟수(GO n)를 붙일 수 없습니다. GO만 한 줄에 쓰세요."
                    : $"구문 오류({e.Line}줄 {e.Column}열): {e.Message}",
                DocRef = "D5",
            };
        }

        Diagnostic EmptyDiagnostic() => new()
        {
            StatementIndex = null,
            Span = lines.Span(0, Math.Max(1, sql.Length)),
            Severity = Severity.Error,
            RuleId = AnalysisRules.RejectEmptyScript,
            Message = "실행할 문장이 없습니다(빈 스크립트이거나 주석뿐입니다).",
            DocRef = "D5",
        };

        static bool OverlapsError(TSqlStatement s, IList<ParseError> errors)
        {
            var end = s.StartOffset + s.FragmentLength;
            return errors.Any(e => e.Offset >= s.StartOffset && e.Offset <= end);
        }

        // ---- 문장 ----

        void Reject(string rule, string message, string docRef, int index, SourceSpan span) =>
            rejects.Add(new Diagnostic { StatementIndex = index, Span = span, Severity = Severity.Error, RuleId = rule, Message = message, DocRef = docRef });

        void Warn(string rule, string message, string docRef, int index, SourceSpan span) =>
            warnings.Add(new Diagnostic { StatementIndex = index, Span = span, Severity = Severity.Warning, RuleId = rule, Message = message, DocRef = docRef });

        void AnalyzeStatement(TSqlStatement s, int index, int batch, int position)
        {
            var span = lines.Span(s);
            if (s is ExecuteStatement exec)
            {
                AnalyzeExec(exec, index, batch, position, span);
                return;
            }
            if (s is not DataModificationStatement dml || dml is MergeStatement)
            {
                var (rule, message, doc) = Classify(s);
                Reject(rule, message, doc, index, span);
                return;
            }

            ChangeOperation kind;
            TSqlFragment spec;
            TableReference? targetRef;
            FromClause? from = null;
            switch (dml)
            {
                case InsertStatement i: kind = ChangeOperation.Insert; spec = i.InsertSpecification; targetRef = i.InsertSpecification.Target; break;
                case UpdateStatement u: kind = ChangeOperation.Update; spec = u.UpdateSpecification; targetRef = u.UpdateSpecification.Target; from = u.UpdateSpecification.FromClause; break;
                case DeleteStatement d: kind = ChangeOperation.Delete; spec = d.DeleteSpecification; targetRef = d.DeleteSpecification.Target; from = d.DeleteSpecification.FromClause; break;
                default:
                    var (rule, message, doc) = Classify(s);
                    Reject(rule, message, doc, index, span);
                    return;
            }

            // 1) 문장 안 금지 구문
            var scan = new DmlScan(lines);
            dml.Accept(scan);
            foreach (var f in scan.Findings) Reject(f.Rule, f.Message, f.DocRef, index, f.Span);

            // 2) 대상 테이블
            string? target = null;
            CatalogTable? table = null;
            NamedTableReference? targetNode = null;
            if (targetRef is NamedTableReference nt)
            {
                targetNode = SqlSyntax.ResolveAlias(nt, from);
                var so = targetNode.SchemaObject;
                var isExternalName = so.ServerIdentifier != null || so.DatabaseIdentifier != null;
                var isTemp = so.BaseIdentifier.Value.StartsWith('#');
                if (!isExternalName && !isTemp) // 이 둘은 DmlScan이 이미 거부했다
                {
                    var isCte = so.SchemaIdentifier == null && dml.WithCtesAndXmlNamespaces?.CommonTableExpressions
                        .Any(c => c.ExpressionName.Value.Equals(so.BaseIdentifier.Value, StringComparison.OrdinalIgnoreCase)) == true;
                    (target, table) = resolver.Resolve(so, DefaultSchema);
                    var targetSpan = lines.Span(targetNode);
                    if (isCte)
                        Reject(AnalysisRules.RejectTargetNotUserTable, $"CTE '{so.BaseIdentifier.Value}'를 대상으로 하는 {Verb(kind)}는 지원하지 않습니다. 기반 테이블을 직접 지정하세요.", "D5", index, targetSpan);
                    else if (table is null)
                        Reject(AnalysisRules.RejectTargetNotUserTable, $"'{target}'은(는) 기반 사용자 테이블이 아닙니다(뷰·동의어·없는 이름 포함).", "D5", index, targetSpan);
                    else if (table.Schema.Equals("audit", StringComparison.OrdinalIgnoreCase))
                        Reject(AnalysisRules.RejectCaptureStore, $"'{target}'은(는) 변경 수집 장치의 저장소입니다. 사용자 문장의 대상이 될 수 없습니다.", "D5", index, targetSpan);
                    else
                    {
                        CheckTargetTriggers(target, table, kind, index, lines.Span(targetNode));
                        // 직접 대상의 트리거는 CheckTargetTriggers가 이미 판정했다. 쓰기 대상만 넓힌다.
                        CheckReachedTriggers([(target, kind, $"{target} {Verb(kind)}")],
                            new HashSet<string>(Firing(target, kind).Select(t => t.Trigger), StringComparer.OrdinalIgnoreCase), index, lines.Span(targetNode));
                    }
                    if (isCte) target = null; // CTE 이름은 테이블 이름이 아니다
                }
            }

            // 3) 경고: TOP, 비결정 함수
            CheckTop(dml, spec, kind, index, span);
            var nondeterminism = SqlSyntax.FindNondeterminism(dml);
            var nondeterministicColumns = kind == ChangeOperation.Delete
                ? []
                : SqlSyntax.NondeterministicColumns(spec, () => SqlSyntax.InsertableColumns(baseline.Catalog, table));
            if (table is not null)
            {
                var canonical = baseline.Catalog.Columns.Where(c => c.ObjectId == table.ObjectId).Select(c => c.Name).ToList();
                nondeterministicColumns = nondeterministicColumns
                    .Select(c => canonical.FirstOrDefault(k => k.Equals(c, StringComparison.OrdinalIgnoreCase)) ?? c).ToList();
            }
            if (nondeterminism.Count > 0)
            {
                var names = nondeterminism.Select(n => n.Name).Distinct().ToList();
                Warn(AnalysisRules.WarnNondeterministicFunction,
                    $"문장 안 비결정 함수({string.Join(", ", names)})가 있어 실행마다 값이 달라질 수 있습니다." +
                    (nondeterministicColumns.Count > 0 ? $" 영향 열: {string.Join(", ", nondeterministicColumns)}." : ""),
                    "E1", index, lines.Span(nondeterminism[0].Node));
            }

            if (target is null) return; // 이름을 정할 수 없는 대상(임시 테이블, 변수, 다른 DB, CTE): 이미 거부됨
            statements.Add(new AnalyzedStatement
            {
                Index = index,
                Batch = batch,
                Position = position,
                Kind = kind,
                Target = target,
                Span = span,
                Sql = sql.Substring(s.StartOffset, s.FragmentLength),
                NondeterministicColumns = nondeterministicColumns,
            });
        }

        static string Verb(ChangeOperation k) => k switch
        {
            ChangeOperation.Insert => "INSERT",
            ChangeOperation.Update => "UPDATE",
            ChangeOperation.Delete => "DELETE",
            ChangeOperation.Exec => "EXEC",
            _ => throw new ArgumentOutOfRangeException(nameof(k), k, null),
        };

        void CheckTop(DataModificationStatement dml, TSqlFragment spec, ChangeOperation kind, int index, SourceSpan span)
        {
            TSqlFragment? offender = null;
            // UPDATE·DELETE(·INSERT) 자체의 TOP은 ORDER BY를 붙일 수 없다.
            switch (spec)
            {
                case UpdateSpecification { TopRowFilter: { } t }: offender = t; break;
                case DeleteSpecification { TopRowFilter: { } t }: offender = t; break;
                case InsertSpecification { TopRowFilter: { } t }: offender = t; break;
            }
            // 그 밖에 하위 쿼리의 TOP(ORDER BY 없음)
            offender ??= new TopFinder().Find(dml);
            if (offender is null) return;
            Warn(AnalysisRules.WarnTopWithoutOrder,
                $"ORDER BY 없는 TOP이 붙은 {Verb(kind)}는 어느 행이 선택될지 정해져 있지 않아 실행마다 결과가 달라질 수 있습니다.",
                "D5", index, lines.Span(offender));
        }

        sealed class TopFinder : TSqlFragmentVisitor
        {
            TSqlFragment? found;
            public TSqlFragment? Find(TSqlFragment f) { f.Accept(this); return found; }
            public override void Visit(QuerySpecification n)
            {
                if (found == null && n.TopRowFilter != null && n.OrderByClause == null) found = n.TopRowFilter;
            }
        }

        /// <summary>대상 테이블에 붙은 트리거와 감사 순서로 판정하는 D5 규칙(거부 3종, 경고 2종).</summary>
        void CheckTargetTriggers(string target, CatalogTable table, ChangeOperation kind, int index, SourceSpan span)
        {
            var firing = baseline.Triggers
                .Where(t => t.Table.Equals(target, StringComparison.OrdinalIgnoreCase) && !t.IsDisabled && !t.IsAuditTrigger && t.Events.Contains(kind))
                .ToList();

            foreach (var t in firing.Where(t => t.IsEncrypted))
                Reject(AnalysisRules.RejectTriggerOpaque,
                    $"{target}에 본문을 읽을 수 없는(암호화·CLR) 트리거 {t.Trigger}가 있어 외부 부작용을 배제할 수 없습니다.", "E18", index, span);

            foreach (var t in firing.Where(t => !t.IsEncrypted && t.ExternalSideEffects.Count > 0))
            {
                var unparsed = t.ExternalSideEffects.Contains(TriggerAnalyzer.UnparsedMarker);
                Reject(AnalysisRules.RejectTriggerExternalEffect,
                    unparsed
                        ? $"{target}의 트리거 {t.Trigger}의 본문을 분석하지 못해 외부 부작용을 배제할 수 없습니다."
                        : $"{target}의 트리거 {t.Trigger}에 외부 부작용({string.Join(", ", t.ExternalSideEffects)})이 있어 샌드박스에서 재현하면 실제 부작용이 생길 수 있습니다.",
                    "E18", index, span);
            }

            foreach (var t in firing.Where(t => t.HasTransactionControl))
                Reject(AnalysisRules.RejectTriggerTransactionControl,
                    $"{target}의 트리거 {t.Trigger}가 COMMIT·BEGIN TRAN·SAVE TRAN을 해서 미리보기 트랜잭션이 확정될 수 있습니다(롤백으로 되돌릴 수 없음).",
                    "E13", index, span);

            var dynamic = firing.FirstOrDefault(t => !t.IsEncrypted && t.HasDynamicSql && !t.ExternalSideEffects.Contains(TriggerAnalyzer.UnparsedMarker));
            if (dynamic is not null)
                Warn(AnalysisRules.WarnTriggerDynamicSql,
                    $"{target}의 트리거 {dynamic.Trigger}가 동적 SQL을 써서 모든 테이블에 쓸 수 있는 것으로 봅니다. 원인 귀속 확신도가 낮아지고, 다른 DB에 쓰는 동적 SQL은 감지하지 못합니다.",
                    "E8", index, span);

            var manifest = baseline.Manifest.Tables.FirstOrDefault(m => m.Table.Equals(target, StringComparison.OrdinalIgnoreCase));
            if (manifest is not null)
            {
                var order = kind switch
                {
                    ChangeOperation.Insert => manifest.AuditOrder.Insert,
                    ChangeOperation.Update => manifest.AuditOrder.Update,
                    ChangeOperation.Delete => manifest.AuditOrder.Delete,
                    _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "행 연산만"),
                };
                if (!string.Equals(order, DbRipple.Core.Capture.CaptureManifest.Last, StringComparison.OrdinalIgnoreCase))
                    Warn(AnalysisRules.WarnAuditNotLast,
                        $"{target}의 {Verb(kind)}에는 사용자 Last 트리거가 있어 감사 트리거를 Last로 둘 수 없습니다. 이 테이블에서 발동한 트리거의 원인 귀속은 ambiguous 이하가 됩니다.",
                        "E15", index, span);
            }
        }

        // ---- EXEC 미리보기(2026-11-13): EXEC [스키마.]프로시저 @p = <리터럴>, ... 한 문장 ----

        static readonly HashSet<string> DynamicProcedures = new(["sp_executesql", "sp_prepare", "sp_prepexec", "sp_execute"], StringComparer.OrdinalIgnoreCase);

        void AnalyzeExec(ExecuteStatement s, int index, int batch, int position, SourceSpan span)
        {
            var spec = s.ExecuteSpecification;
            const string dynamicMessage = "EXEC('...')·EXEC @변수·sp_executesql은 동적 SQL이라 지원하지 않습니다. EXEC 스키마.프로시저 @매개변수 = 값 모양으로 쓰세요.";
            if (spec.ExecutableEntity is not ExecutableProcedureReference pr || pr.ProcedureReference.ProcedureReference is not { } nameRef
                || DynamicProcedures.Contains(nameRef.Name.BaseIdentifier.Value))
            {
                Reject(AnalysisRules.RejectExec, dynamicMessage, "E9", index, span);
                return;
            }

            // 1) 호출 모양(사용자 입력)
            var form = new List<string>();
            if (s.Options.Count > 0) form.Add("WITH 옵션(RESULT SETS·RECOMPILE)");
            if (spec.Variable is not null) form.Add("반환 값 변수(EXEC @rc = ...)");
            if (spec.LinkedServer is not null) form.Add("AT 링크드 서버");
            if (spec.ExecuteContext is not null) form.Add("EXECUTE ... AS 문맥");
            if (pr.AdHocDataSource is not null) form.Add("OPENDATASOURCE");
            if (nameRef.Number is not null) form.Add("번호 프로시저(;n)");
            var args = new List<(string Name, string? Value, ExecuteParameter Node)>();
            foreach (var p in pr.Parameters)
            {
                if (p.Variable is null) { form.Add("위치 매개변수(이름을 붙여 @p = 값으로 쓰세요)"); continue; }
                if (p.IsOutput) form.Add($"OUTPUT 매개변수 {p.Variable.Name}");
                var value = ExecSqlBuilder.RenderLiteral(p.ParameterValue);
                if (value is null)
                {
                    if (p.ParameterValue is VariableReference or GlobalVariableExpression)
                        Reject(AnalysisRules.RejectVariableOrFlow, $"매개변수 {p.Variable.Name}에 변수를 쓸 수 없습니다. 값을 직접 쓰세요.", "E9", index, lines.Span(p));
                    else form.Add($"{p.Variable.Name}의 값(리터럴·음수 리터럴·NULL·DEFAULT만)");
                }
                if (args.Any(x => x.Name.Equals(p.Variable.Name, StringComparison.OrdinalIgnoreCase))) form.Add($"중복 매개변수 {p.Variable.Name}");
                args.Add((p.Variable.Name, value, p));
            }
            if (form.Count > 0)
                Reject(AnalysisRules.RejectProcCallForm, $"지원하지 않는 EXEC 모양입니다: {string.Join(", ", form.Distinct())}.", "E9", index, span);

            // 2) 대상 프로시저: 카탈로그 T-SQL 사용자 프로시저, 1·2부분 이름, sp_ 접두 아님
            var pn = nameRef.Name;
            if (pn.ServerIdentifier is not null || pn.DatabaseIdentifier is not null)
            {
                Reject(AnalysisRules.RejectCrossDatabase, $"3·4부분 이름 '{SqlSyntax.Name(pn)}'은 샌드박스 밖의 상태라 쓸 수 없습니다. 스키마.프로시저만 쓰세요.", "A18", index, lines.Span(pn));
                return;
            }
            if (pn.BaseIdentifier.Value.StartsWith("sp_", StringComparison.OrdinalIgnoreCase))
            {
                Reject(AnalysisRules.RejectProcNotFound, $"sp_ 접두 프로시저 '{SqlSyntax.Name(pn)}'은 시스템 프로시저로 해석될 수 있어 지원하지 않습니다.", "E9", index, lines.Span(pn));
                return;
            }
            var module = Procedures.Find(pn, DefaultSchema);
            if (module is null)
            {
                Reject(AnalysisRules.RejectProcNotFound, $"'{SqlSyntax.Name(pn)}'은(는) 이 DB 카탈로그의 사용자 저장 프로시저가 아니거나 대소문자만 다른 이름이 여럿이라 정할 수 없습니다.", "E9", index, lines.Span(pn));
                return;
            }

            var info = Procedures.Analyze(module);
            var proc = info.Procedure;
            var execSql = "";
            if (info.Opaque)
                Reject(AnalysisRules.RejectProcOpaque, $"{proc}의 본문을 읽을 수 없습니다(암호화·CLR 또는 파싱 실패).", "E18", index, span);
            else
            {
                var paramOk = CheckParameters(info, args, proc, index, span);
                CheckProcedureBody(info, index, span);
                if (form.Count == 0 && paramOk && args.All(a => a.Value is not null))
                {
                    var byName = info.Parameters.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
                    execSql = ExecSqlBuilder.Build(module.Schema, module.Name, args.Select(a => (byName[a.Name].Name, a.Value!)).ToList());
                }
            }

            statements.Add(new AnalyzedStatement
            {
                Index = index,
                Batch = batch,
                Position = position,
                Kind = ChangeOperation.Exec,
                Target = null,
                Span = span,
                Sql = sql.Substring(s.StartOffset, s.FragmentLength),
                Procedure = proc,
                Parameters = args.Select(a => sql.Substring(a.Node.StartOffset, a.Node.FragmentLength)).ToList(),
                Plan = new ProcedurePlan { Procedure = proc, ExecSql = execSql, WriteTargets = info.WriteTargets, ExpandedCalls = info.ExpandedCalls },
            });
        }

        /// <summary>매개변수 대조: 없는 이름, 기본값 없는 필수 매개변수 누락(DEFAULT 포함), OUTPUT·READONLY 매개변수(1차 범위 밖).</summary>
        bool CheckParameters(ProcedureStaticInfo info, List<(string Name, string? Value, ExecuteParameter Node)> args, string proc, int index, SourceSpan span)
        {
            var problems = new List<string>();
            foreach (var p in info.Parameters.Where(p => p.IsOutput || p.IsReadOnly))
                problems.Add($"{p.Name}이(가) {(p.IsOutput ? "OUTPUT" : "READONLY(테이블 값)")} 매개변수(1차 범위 밖)");
            foreach (var a in args.Where(a => !info.Parameters.Any(p => p.Name.Equals(a.Name, StringComparison.OrdinalIgnoreCase))))
                problems.Add($"{a.Name}은(는) {proc}의 매개변수가 아님");
            foreach (var p in info.Parameters.Where(p => !p.HasDefault && !p.IsOutput && !p.IsReadOnly))
            {
                var given = args.FirstOrDefault(a => a.Name.Equals(p.Name, StringComparison.OrdinalIgnoreCase));
                if (given.Name is null || given.Value == "DEFAULT") problems.Add($"기본값 없는 {p.Name}에 값이 없음");
            }
            if (problems.Count == 0) return true;
            Reject(AnalysisRules.RejectProcParameter, $"매개변수가 {proc} 정의와 맞지 않습니다: {string.Join(", ", problems)}.", "E9", index, span);
            return false;
        }

        /// <summary>펼친 본문 규칙(거부·경고), 쓰기 대상 검사, 도달 폐포(쓰기 대상 × 세 연산에서 FK·트리거로 고정점까지).</summary>
        void CheckProcedureBody(ProcedureStaticInfo info, int index, SourceSpan span)
        {
            var proc = info.Procedure;
            // 규칙당 진단 1개(D5): 본문 규칙 사실, 못 펼친 호출, 외부 부작용 표시를 규칙별로 모아 한 문장으로 낸다.
            var details = info.Rejects.GroupBy(f => f.RuleId).ToDictionary(g => g.Key, g => g.Select(f => f.Detail).Distinct().ToList());
            void More(string rule, string detail)
            {
                if (!details.TryGetValue(rule, out var l)) details[rule] = l = [];
                l.Add(detail);
            }
            if (info.UnresolvedCalls.Count > 0)
                More(AnalysisRules.RejectProcUnresolvedCall,
                    $"부르는 프로시저를 펼치지 못해 쓰기 대상을 다 알 수 없음 {string.Join(", ", info.UnresolvedCalls)}(카탈로그에 없음·이름이 모호함·본문 없음·동적 SQL·재귀·깊이 {ProcedureAnalyzer.MaxProcedureDepth} 초과)");
            if (info.External.Count > 0)
                More(AnalysisRules.RejectProcExternalEffect,
                    $"외부 부작용({string.Join(", ", info.External)})이 있어 샌드박스에서 실행하면 실제 부작용이 생길 수 있음");
            foreach (var (rule, list) in details.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                Reject(rule, $"{proc}의 펼친 본문: {string.Join("; ", list)}.", ProcDocRef(rule), index, span);

            var start = new List<(string, ChangeOperation, string)>();
            var bad = new List<string>();
            foreach (var w in info.WriteTargets)
            {
                var dot = w.IndexOf('.');
                var table = dot < 0 ? null : resolver.Find(w[..dot], w[(dot + 1)..]);
                if (table is null || table.Schema.Equals("audit", StringComparison.OrdinalIgnoreCase)) { bad.Add(w); continue; }
                foreach (var op in AllOps) start.Add((w, op, $"EXEC {proc} → {w}"));
            }
            if (bad.Count > 0)
                Reject(AnalysisRules.RejectProcWriteTarget, $"{proc}의 쓰기 대상 {string.Join(", ", bad)}은(는) 기반 사용자 테이블이 아닙니다(뷰·동의어·없는 이름·감사 저장소).", "D5", index, span);
            CheckReachedTriggers(start, new HashSet<string>(StringComparer.OrdinalIgnoreCase), index, span);

            foreach (var g in info.Warnings.GroupBy(f => f.RuleId).OrderBy(g => g.Key, StringComparer.Ordinal))
                Warn(g.Key, $"{proc}의 펼친 본문: {string.Join("; ", g.Select(f => f.Detail).Distinct())}.", ProcDocRef(g.Key), index, span);
            if (info.NondeterministicWrites.Count > 0)
                Warn(AnalysisRules.WarnProcNondeterministicWrite,
                    $"{proc}가 비결정 값으로 쓰는 열: {string.Join(", ", info.NondeterministicWrites.Select(w => w.Table + "." + w.Column))}. 실행마다 값이 달라질 수 있습니다.", "E1", index, span);
        }

        static string ProcDocRef(string rule) => rule switch
        {
            AnalysisRules.RejectProcExternalEffect => "E18",
            AnalysisRules.RejectProcDynamicSql or AnalysisRules.RejectProcUnresolvedCall => "E8",
            AnalysisRules.RejectProcSavepoint or AnalysisRules.RejectProcUnbalancedCommit or AnalysisRules.RejectProcTrancountBranch
                or AnalysisRules.WarnProcNestedTransaction or AnalysisRules.WarnProcRollback or AnalysisRules.WarnProcXactStateCheck => "E13",
            AnalysisRules.RejectProcDdl => "E10",
            _ => "E9",
        };

        // ---- 도달 폐포(2026-11-13): FK 참조 동작과 트리거 쓰기 대상으로 닿는 테이블의 트리거도 D5 거부 규칙으로 본다 ----

        static readonly ChangeOperation[] AllOps = [ChangeOperation.Insert, ChangeOperation.Update, ChangeOperation.Delete];

        IEnumerable<TriggerStaticInfo> Firing(string table, ChangeOperation op) => baseline.Triggers
            .Where(t => t.Table.Equals(table, StringComparison.OrdinalIgnoreCase) && !t.IsDisabled && !t.IsAuditTrigger && t.Events.Contains(op));

        /// <summary>
        /// 직접 대상 밖에서 발동할 수 있는 트리거를 (테이블, 연산) 고정점까지 모아 외부 부작용·불투명·트랜잭션 제어를 거부한다.
        /// 넓히는 길은 둘이다: FK 참조 동작(CASCADE는 같은 연산, SET NULL·SET DEFAULT는 자식 UPDATE)과 트리거의 정적 쓰기 대상(연산은 모르므로 셋 다).
        /// 쓰기 대상을 모르는 트리거(암호화, 파싱 실패, 동적 SQL, 못 펼친 프로시저)는 모든 사용자 테이블에 닿는다고 보고 닫힌 쪽으로 넓힌다.
        /// ROLLBACK만 하는 트리거는 거부하지 않는다(직접 대상 규칙과 같이 trigger_blocked로 보인다).
        /// </summary>
        void CheckReachedTriggers(IEnumerable<(string Table, ChangeOperation Op, string Via)> start, HashSet<string> judged, int index, SourceSpan span)
        {
            var catalog = baseline.Catalog;
            var nameById = catalog.Tables.ToDictionary(t => t.ObjectId, t => $"{t.Schema}.{t.Name}");
            var idByName = catalog.Tables.ToDictionary(t => $"{t.Schema}.{t.Name}", t => t.ObjectId, StringComparer.OrdinalIgnoreCase);
            var childFks = catalog.ForeignKeys.Where(f => !f.IsDisabled)
                .GroupBy(f => f.ObjectId).Select(g => g.First())
                .ToLookup(f => f.ReferencedObjectId);

            var seen = new HashSet<(string, ChangeOperation)>();
            var queue = new Queue<(string Table, ChangeOperation Op, string Via)>();
            void Add(string table, ChangeOperation op, string via)
            {
                if (seen.Add((table.ToUpperInvariant(), op))) queue.Enqueue((table, op, via));
            }

            var allTablesReached = false;
            foreach (var (t0, op0, via0) in start)
            {
                if (op0 == ChangeOperation.Exec) throw new ArgumentOutOfRangeException(nameof(start), "행 연산만");
                Add(t0, op0, via0);
            }

            while (queue.Count > 0)
            {
                var (table, op, via) = queue.Dequeue();

                if (op != ChangeOperation.Insert && idByName.TryGetValue(table, out var id))
                    foreach (var fk in childFks[id])
                    {
                        var action = op == ChangeOperation.Delete ? fk.DeleteAction : fk.UpdateAction;
                        ChangeOperation? childOp = action.ToUpperInvariant() switch
                        {
                            "CASCADE" => op,
                            "SET_NULL" or "SET_DEFAULT" => ChangeOperation.Update,
                            _ => null,
                        };
                        if (childOp is { } co && nameById.TryGetValue(fk.ParentObjectId, out var child))
                            Add(child, co, $"{via} → FK {fk.Name} → {child} {Verb(co)}");
                    }

                foreach (var t in Firing(table, op))
                {
                    if (judged.Add(t.Trigger)) RejectReached(t, via, index, span);

                    foreach (var w in t.WriteTargets)
                        foreach (var wo in AllOps)
                            Add(w, wo, $"{via} → 트리거 {t.Trigger} → {w}");

                    var unknownWrites = t.IsEncrypted || t.WriteTargetsUnknown || t.HasDynamicSql || t.UnresolvedProcedureCalls.Count > 0
                        || (t.ProcedureCalls.Count > 0 && t.ExpandedProcedureCalls.Count == 0); // 펼치기 전 분석기 호환 규칙(TriggerStaticInfo 주석)
                    if (unknownWrites && !allTablesReached)
                    {
                        allTablesReached = true;
                        foreach (var name in nameById.Values.Where(n => !n.StartsWith("audit.", StringComparison.OrdinalIgnoreCase)))
                            foreach (var wo in AllOps)
                                Add(name, wo, $"{via} → 트리거 {t.Trigger}(쓰기 대상 미상) → {name}");
                    }
                }
            }
        }

        void RejectReached(TriggerStaticInfo t, string via, int index, SourceSpan span)
        {
            var path = $" 닿는 경로: {via} → 트리거 {t.Trigger}.";
            if (t.IsEncrypted)
                Reject(AnalysisRules.RejectTriggerOpaque,
                    $"FK 연쇄·트리거로 닿는 {t.Table}에 본문을 읽을 수 없는(암호화·CLR) 트리거 {t.Trigger}가 있어 외부 부작용을 배제할 수 없습니다.{path}", "E18", index, span);
            else if (t.ExternalSideEffects.Count > 0)
                Reject(AnalysisRules.RejectTriggerExternalEffect,
                    t.ExternalSideEffects.Contains(TriggerAnalyzer.UnparsedMarker)
                        ? $"FK 연쇄·트리거로 닿는 {t.Table}의 트리거 {t.Trigger}의 본문을 분석하지 못해 외부 부작용을 배제할 수 없습니다.{path}"
                        : $"FK 연쇄·트리거로 닿는 {t.Table}의 트리거 {t.Trigger}에 외부 부작용({string.Join(", ", t.ExternalSideEffects)})이 있어 샌드박스에서 재현하면 실제 부작용이 생길 수 있습니다.{path}",
                    "E18", index, span);
            if (t.HasTransactionControl)
                Reject(AnalysisRules.RejectTriggerTransactionControl,
                    $"FK 연쇄·트리거로 닿는 {t.Table}의 트리거 {t.Trigger}가 COMMIT·BEGIN TRAN·SAVE TRAN을 해서 미리보기 트랜잭션이 확정될 수 있습니다(롤백으로 되돌릴 수 없음).{path}",
                    "E13", index, span);
        }

        // ---- 허용 목록 밖 문장 분류 ----

        static readonly HashSet<string> TransactionTypes = ["BeginTransactionStatement", "CommitTransactionStatement", "RollbackTransactionStatement", "SaveTransactionStatement"];
        static readonly HashSet<string> SetTypes =
        [
            "PredicateSetStatement", "SetIdentityInsertStatement", "SetRowCountStatement", "SetTextSizeStatement", "SetTransactionIsolationLevelStatement",
            "SetOffsetsStatement", "SetStatisticsStatement", "SetErrorLevelStatement", "SetFipsFlaggerCommand", "SetCommandStatement", "SetUserStatement",
        ];
        static readonly HashSet<string> FlowTypes =
        [
            "DeclareVariableStatement", "DeclareTableVariableStatement", "SetVariableStatement", "IfStatement", "WhileStatement", "BeginEndBlockStatement",
            "BeginEndAtomicBlockStatement", "TryCatchStatement", "ReturnStatement", "GoToStatement", "LabelStatement", "BreakStatement", "ContinueStatement",
            "ThrowStatement", "RaiseErrorStatement", "PrintStatement", "AssignmentSetClause",
        ];
        static readonly HashSet<string> AdminTypes =
        [
            "WaitForStatement", "DbccStatement", "BackupDatabaseStatement", "BackupTransactionLogStatement", "BackupStatement", "RestoreStatement",
            "KillStatement", "ShutdownStatement", "CheckpointStatement", "ReconfigureStatement", "BulkInsertBase",
        ];
        static readonly HashSet<string> OtherDdlTypes = ["UpdateStatisticsStatement", "EnableDisableTriggerStatement", "RenameEntityStatement", "MoveConversationStatement"];

        static (string Rule, string Message, string DocRef) Classify(TSqlStatement s)
        {
            var type = s.GetType().Name;
            if (s is MergeStatement)
                return (AnalysisRules.RejectMerge, "MERGE는 액션별 발동과 MERGE 전체 @@ROWCOUNT 처리가 필요해 지원하지 않습니다. INSERT/UPDATE/DELETE로 나눠 쓰세요.", "E12");
            if (s is TruncateTableStatement)
                return (AnalysisRules.RejectTruncate, "TRUNCATE는 DELETE 트리거를 발동하지 않아 미리보기할 수 없습니다.", "A21");
            if (type is "BulkInsertStatement" or "InsertBulkStatement")
                return (AnalysisRules.RejectBulkInsert, "BULK INSERT는 트리거가 발동하지 않을 수 있어 미리보기할 수 없습니다.", "A21");
            if (TransactionTypes.Contains(type))
                return (AnalysisRules.RejectTransactionControl, "트랜잭션 제어문은 쓸 수 없습니다. 도구가 트랜잭션을 소유하고 끝에 롤백합니다.", "D5");
            if (type == "UseStatement")
                return (AnalysisRules.RejectUse, "USE는 쓸 수 없습니다. 샌드박스 DB가 고정되어 있습니다.", "D5");
            if (SetTypes.Contains(type))
                return (AnalysisRules.RejectSetOption, "SET 옵션은 쓸 수 없습니다. 도구가 세션 옵션을 고정합니다.", "D5");
            if (s is ExecuteStatement)
                return (AnalysisRules.RejectExec, "EXEC·sp_executesql은 문장별 실행으로 의미가 바뀌어 지원하지 않습니다.", "E9");
            if (FlowTypes.Contains(type))
                return (AnalysisRules.RejectVariableOrFlow, "변수와 제어 흐름(DECLARE, SET @v, IF, WHILE, TRY...CATCH, BEGIN...END 등)은 지원하지 않습니다. 문장을 하나씩 쓰세요.", "E9");
            if (AdminTypes.Contains(type))
                return (AnalysisRules.RejectAdminCommand, $"{type.Replace("Statement", "")} 같은 관리 명령은 범위 밖입니다.", "D5");
            if (s is SelectStatement { Into: not null })
                return (AnalysisRules.RejectSelectInto, "SELECT ... INTO는 테이블을 만드는 DDL이라 미리보기할 수 없습니다.", "A16");
            if (s is SelectStatement)
                return (AnalysisRules.RejectNotAllowed, "SELECT는 변경 미리보기 대상이 아닙니다. 허용되는 문장은 INSERT, UPDATE, DELETE뿐입니다.", "D5");
            if (type.StartsWith("Create", StringComparison.Ordinal) || type.StartsWith("Alter", StringComparison.Ordinal)
                || type.StartsWith("Drop", StringComparison.Ordinal) || OtherDdlTypes.Contains(type))
                return (AnalysisRules.RejectDdl, "DDL은 미리보기할 수 없습니다. 스키마를 바꾸면 트랜잭션 제약과 DDL 트리거 반응이 달라집니다.", "E10");
            return (AnalysisRules.RejectNotAllowed, $"허용되지 않는 문장({type.Replace("Statement", "")})입니다. 허용되는 문장은 INSERT, UPDATE, DELETE뿐입니다.", "D5");
        }
    }

    /// <summary>허용 DML 안에서 거부 대상 구문(OUTPUT, 외부 행 집합, 변수, 임시·변수 테이블, 3·4부분 이름 등)을 찾는다. 규칙당 첫 발생만 남긴다.</summary>
    sealed class DmlScan(LineIndex lines) : TSqlFragmentVisitor
    {
        public readonly List<(string Rule, string Message, string DocRef, SourceSpan Span)> Findings = [];
        readonly HashSet<string> seen = [];

        void Add(string rule, string message, string doc, TSqlFragment node)
        {
            if (seen.Add(rule)) Findings.Add((rule, message, doc, lines.Span(node)));
        }

        public override void Visit(OutputClause n) => Add(AnalysisRules.RejectOutput, "OUTPUT 절은 샌드박스의 감사 트리거 때문에 원본과 달리 실패할 수 있어 지원하지 않습니다. OUTPUT 없이 미리보기하세요.", "E11", n);
        public override void Visit(OutputIntoClause n) => Add(AnalysisRules.RejectOutput, "OUTPUT 절은 샌드박스의 감사 트리거 때문에 원본과 달리 실패할 수 있어 지원하지 않습니다. OUTPUT 없이 미리보기하세요.", "E11", n);

        const string ExternalRowsetMessage = "OPENROWSET·OPENDATASOURCE·OPENQUERY는 샌드박스 밖의 원본을 읽어 재현이 보장되지 않아 지원하지 않습니다.";
        public override void Visit(OpenRowsetTableReference n) => Add(AnalysisRules.RejectExternalRowset, ExternalRowsetMessage, "D5", n);
        public override void Visit(OpenQueryTableReference n) => Add(AnalysisRules.RejectExternalRowset, ExternalRowsetMessage, "D5", n);
        public override void Visit(AdHocTableReference n) => Add(AnalysisRules.RejectExternalRowset, ExternalRowsetMessage, "D5", n);
        public override void Visit(BulkOpenRowset n) => Add(AnalysisRules.RejectExternalRowset, ExternalRowsetMessage, "D5", n);

        public override void Visit(TableHint n)
        {
            if (n.HintKind == TableHintKind.IgnoreTriggers)
                Add(AnalysisRules.RejectIgnoreTriggers, "IGNORE_TRIGGERS 힌트는 트리거를 건너뛰어 변경 수집이 깨지므로 쓸 수 없습니다.", "D5", n);
        }

        public override void Visit(NamedTableReference n)
        {
            var so = n.SchemaObject;
            if (so.ServerIdentifier != null || so.DatabaseIdentifier != null)
                Add(AnalysisRules.RejectCrossDatabase, $"3·4부분 이름 '{SqlSyntax.Name(so)}'은 샌드박스 밖의 상태라 쓸 수 없습니다. 스키마.이름만 쓰세요.", "A18", n);
            else if (so.BaseIdentifier.Value.StartsWith('#'))
                Add(AnalysisRules.RejectTempOrTableVariable, "#임시 테이블은 지원하지 않습니다.", "D5", n);
        }

        public override void Visit(SchemaObjectFunctionTableReference n)
        {
            var so = n.SchemaObject;
            if (so.ServerIdentifier != null || so.DatabaseIdentifier != null)
                Add(AnalysisRules.RejectCrossDatabase, $"3·4부분 이름 '{SqlSyntax.Name(so)}'은 샌드박스 밖의 상태라 쓸 수 없습니다.", "A18", n);
        }

        public override void Visit(FunctionCall n)
        {
            if (n.CallTarget is MultiPartIdentifierCallTarget { MultiPartIdentifier.Identifiers.Count: >= 2 })
                Add(AnalysisRules.RejectCrossDatabase, "3·4부분 이름 함수 호출은 샌드박스 밖의 상태라 쓸 수 없습니다.", "A18", n);
        }

        public override void Visit(NextValueForExpression n)
        {
            var so = n.SequenceName;
            if (so.ServerIdentifier != null || so.DatabaseIdentifier != null)
                Add(AnalysisRules.RejectCrossDatabase, $"3·4부분 이름 '{SqlSyntax.Name(so)}'은 샌드박스 밖의 상태라 쓸 수 없습니다.", "A18", n);
        }

        public override void Visit(VariableTableReference n) => Add(AnalysisRules.RejectTempOrTableVariable, "테이블 변수는 지원하지 않습니다.", "D5", n);
        public override void Visit(VariableReference n) => Add(AnalysisRules.RejectVariableOrFlow, "변수(@v)는 지원하지 않습니다. 값을 직접 쓰세요.", "E9", n);
        public override void Visit(GlobalVariableExpression n) => Add(AnalysisRules.RejectVariableOrFlow, "전역 변수(@@...)는 문장별 실행으로 의미가 바뀌어 지원하지 않습니다.", "E9", n);
        public override void Visit(ExecuteInsertSource n) => Add(AnalysisRules.RejectExec, "INSERT ... EXEC는 지원하지 않습니다.", "E9", n);
        public override void Visit(WhereClause n)
        {
            if (n.Cursor != null) Add(AnalysisRules.RejectNotAllowed, "WHERE CURRENT OF(커서)는 지원하지 않습니다.", "D5", n);
        }
    }
}
