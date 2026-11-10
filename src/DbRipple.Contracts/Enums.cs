using System.Text.Json.Serialization;

namespace DbRipple.Contracts;

// 직렬화 값은 설계 문서의 JSON 값 그대로다(JsonStringEnumMemberName). C# 이름은 PascalCase.
// 키 종류(PK, UNIQUE, ROW)는 Core의 DbRipple.Core.Capture.KeyKind를 그대로 쓴다(이름 = 직렬화 값).

/// <summary>미리보기 결과 상태(§6.2 PreviewResult.status). 거부(rejected)는 저장하지 않고 422로만 돌려주므로 여기 없다.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PreviewStatus>))]
public enum PreviewStatus
{
    /// <summary>모든 문장 성공, 상한 이내.</summary>
    [JsonStringEnumMemberName("ok")] Ok,
    /// <summary>문장 실행 오류(§5.2 4).</summary>
    [JsonStringEnumMemberName("failed")] Failed,
    /// <summary>트리거가 거부함(ROLLBACK·INSTEAD OF, A22, E13, §5.2 4).</summary>
    [JsonStringEnumMemberName("trigger_blocked")] TriggerBlocked,
    /// <summary>이미지 상한을 넘어 KEYS 모드로 바뀜(§5.2 5, D8).</summary>
    [JsonStringEnumMemberName("truncated")] Truncated,
}

/// <summary>문장 상태(§6.2 Statement.status).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<StatementStatus>))]
public enum StatementStatus
{
    /// <summary>성공.</summary>
    [JsonStringEnumMemberName("ok")] Ok,
    /// <summary>실행 오류.</summary>
    [JsonStringEnumMemberName("failed")] Failed,
    /// <summary>트리거가 거부함.</summary>
    [JsonStringEnumMemberName("trigger_blocked")] TriggerBlocked,
    /// <summary>앞 문장 실패로 실행하지 않음.</summary>
    [JsonStringEnumMemberName("not_run")] NotRun,
}

/// <summary>DML 연산(§6.2 Statement.kind, Firing.operation, RowChange.operation). Core DmlOperation과 같은 뜻이고 직렬화 값만 대문자다.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ChangeOperation>))]
public enum ChangeOperation
{
    /// <summary>INSERT(감사 헤더 operation 'I').</summary>
    [JsonStringEnumMemberName("INSERT")] Insert,
    /// <summary>UPDATE(감사 헤더 operation 'U').</summary>
    [JsonStringEnumMemberName("UPDATE")] Update,
    /// <summary>DELETE(감사 헤더 operation 'D').</summary>
    [JsonStringEnumMemberName("DELETE")] Delete,
}

/// <summary>원인 종류(§6.2 cause.kind).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CauseKind>))]
public enum CauseKind
{
    /// <summary>문장의 직접 대상(§7.2 5단계).</summary>
    [JsonStringEnumMemberName("DIRECT")] Direct,
    /// <summary>FK ON DELETE/UPDATE CASCADE(§7.2 3단계).</summary>
    [JsonStringEnumMemberName("FK_CASCADE")] FkCascade,
    /// <summary>FK SET NULL.</summary>
    [JsonStringEnumMemberName("FK_SET_NULL")] FkSetNull,
    /// <summary>FK SET DEFAULT.</summary>
    [JsonStringEnumMemberName("FK_SET_DEFAULT")] FkSetDefault,
    /// <summary>AFTER 트리거(§7.2 4단계).</summary>
    [JsonStringEnumMemberName("TRIGGER")] Trigger,
    /// <summary>INSTEAD OF 트리거가 원래 동작을 대체(§7.2 5단계).</summary>
    [JsonStringEnumMemberName("INSTEAD_OF")] InsteadOf,
    /// <summary>후보 없음.</summary>
    [JsonStringEnumMemberName("UNEXPLAINED")] Unexplained,
}

/// <summary>확신도(§6.2 cause.confidence, §7.3). 트리거 귀속 상한은 Inferred(MVP).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<Confidence>))]
public enum Confidence
{
    /// <summary>결정적으로 검증됨(직접 대상, §7.2 3단계 certain 조건).</summary>
    [JsonStringEnumMemberName("certain")] Certain,
    /// <summary>규칙상 후보가 하나.</summary>
    [JsonStringEnumMemberName("inferred")] Inferred,
    /// <summary>후보가 여럿.</summary>
    [JsonStringEnumMemberName("ambiguous")] Ambiguous,
    /// <summary>후보 없음.</summary>
    [JsonStringEnumMemberName("unexplained")] Unexplained,
}

/// <summary>귀속 근거(§6.2 cause.evidence).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<Evidence>))]
public enum Evidence
{
    /// <summary>감사 헤더 nestLevel.</summary>
    [JsonStringEnumMemberName("depth")] Depth,
    /// <summary>후위 기록 괄호 추론(§7.2 2단계, H1).</summary>
    [JsonStringEnumMemberName("postorderBracket")] PostorderBracket,
    /// <summary>트리거 본문 정적 쓰기 대상(§7.2 4단계).</summary>
    [JsonStringEnumMemberName("staticWriteSet")] StaticWriteSet,
    /// <summary>FK 키 값 일치(§7.2 3단계).</summary>
    [JsonStringEnumMemberName("fkKeyMatch")] FkKeyMatch,
    /// <summary>카탈로그의 INSTEAD OF 트리거(§7.2 5단계).</summary>
    [JsonStringEnumMemberName("catalogInsteadOf")] CatalogInsteadOf,
    /// <summary>롤백 전 재조회(§7.2 6단계).</summary>
    [JsonStringEnumMemberName("reread")] Reread,
    /// <summary>최종 차이(§7.2 6단계).</summary>
    [JsonStringEnumMemberName("netDiff")] NetDiff,
}

/// <summary>행 이미지 수집 모드(§6.2 imageMode, AuditControl.mode).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ImageMode>))]
public enum ImageMode
{
    /// <summary>행 이미지 있음.</summary>
    [JsonStringEnumMemberName("FULL")] Full,
    /// <summary>키만(상한 초과 뒤, D8).</summary>
    [JsonStringEnumMemberName("KEYS")] Keys,
}

/// <summary>감사 트리거 순서(§6.2 Firing.auditOrder). Core CaptureManifest.Last/None 문자열과 같은 값.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AuditOrder>))]
public enum AuditOrder
{
    /// <summary>그 테이블·연산의 Last로 지정됨.</summary>
    [JsonStringEnumMemberName("LAST")] Last,
    /// <summary>사용자 Last가 있어 지정하지 못함(E15, §7.2 2단계 상한 ambiguous).</summary>
    [JsonStringEnumMemberName("NONE")] None,
}

/// <summary>순서 보장(§6.2 orderGuarantee, §7.2 7단계).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<OrderGuarantee>))]
public enum OrderGuarantee
{
    /// <summary>부모 → 자식 관계가 문서로 보장됨.</summary>
    [JsonStringEnumMemberName("guaranteed")] Guaranteed,
    /// <summary>관측일 뿐(형제·2차 체인 순서, H2·H3·H5).</summary>
    [JsonStringEnumMemberName("observed_only")] ObservedOnly,
}

/// <summary>UPDATE 전후 짝짓기(§6.2 RowChange.pairing, A12, E6·E7).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<Pairing>))]
public enum Pairing
{
    /// <summary>PK로 짝지음.</summary>
    [JsonStringEnumMemberName("BY_PK")] ByPk,
    /// <summary>바뀌지 않은 NOT NULL 유일 키로 짝지음.</summary>
    [JsonStringEnumMemberName("BY_UNIQUE")] ByUnique,
    /// <summary>묶음이 한 행이라 짝이 확정됨.</summary>
    [JsonStringEnumMemberName("SINGLE_ROW")] SingleRow,
    /// <summary>짝 미확정(PK가 바뀐 다중 행 UPDATE, 키 없는 테이블).</summary>
    [JsonStringEnumMemberName("UNPAIRED")] Unpaired,
}

/// <summary>최종 after 출처(§6.2 RowChange.finalAfterSource, §7.2 6단계).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<FinalAfterSource>))]
public enum FinalAfterSource
{
    /// <summary>감사 기록의 after.</summary>
    [JsonStringEnumMemberName("AUDIT")] Audit,
    /// <summary>롤백 전 재조회 값.</summary>
    [JsonStringEnumMemberName("REREAD")] Reread,
}

/// <summary>비결정 열 사유(§6.2 RowChange.volatile[].reason, E1~E4).
/// 주의: Core 매니페스트 VolatileColumn.Reason에는 "COMPUTED"가 있고(계산 열 → RowChange.derived로 옮김) SEQUENCE 기본값은 NONDETERMINISTIC_DEFAULT로 나온다(열린 질문 Q7).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<VolatileReason>))]
public enum VolatileReason
{
    /// <summary>IDENTITY 열(E2).</summary>
    [JsonStringEnumMemberName("IDENTITY")] Identity,
    /// <summary>SEQUENCE 기본값(E3).</summary>
    [JsonStringEnumMemberName("SEQUENCE_DEFAULT")] SequenceDefault,
    /// <summary>GETDATE·NEWID 류 기본값(E1).</summary>
    [JsonStringEnumMemberName("NONDETERMINISTIC_DEFAULT")] NondeterministicDefault,
    /// <summary>사용자 문장 안의 비결정 함수(E1, 배치 분석).</summary>
    [JsonStringEnumMemberName("STATEMENT_FUNCTION")] StatementFunction,
    /// <summary>트리거 본문의 비결정 함수(E1, 트리거 정적 분석).</summary>
    [JsonStringEnumMemberName("TRIGGER_GETDATE")] TriggerGetdate,
    /// <summary>rowversion(E4).</summary>
    [JsonStringEnumMemberName("ROWVERSION")] Rowversion,
}

/// <summary>잔차 범주(§6.2 Residual.category). 원인 후보는 이 순서로 따진다(§7.2 6단계).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ResidualCategory>))]
public enum ResidualCategory
{
    /// <summary>감사 트리거를 깔지 않은 제외 테이블(E17).</summary>
    [JsonStringEnumMemberName("EXCLUDED_TABLE")] ExcludedTable,
    /// <summary>수집 불가 열(text/ntext/image, JSON 키 충돌, §6.1).</summary>
    [JsonStringEnumMemberName("UNOBSERVABLE_COLUMN")] UnobservableColumn,
    /// <summary>같은 테이블 트리거(안전망, E14).</summary>
    [JsonStringEnumMemberName("SAME_TABLE_TRIGGER")] SameTableTrigger,
    /// <summary>알 수 없음.</summary>
    [JsonStringEnumMemberName("UNKNOWN")] Unknown,
}

/// <summary>잔차를 찾은 경로(§6.2 Residual.detectedBy). 설계 예시에는 "REREAD"만 있다. "NET_DIFF" 철자는 이 계약이 정했다(열린 질문 Q8).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ResidualDetectedBy>))]
public enum ResidualDetectedBy
{
    /// <summary>재조회 값이 감사 after와 다름.</summary>
    [JsonStringEnumMemberName("REREAD")] Reread,
    /// <summary>최종 차이에만 있음(감사 기록에 그 행이 없음).</summary>
    [JsonStringEnumMemberName("NET_DIFF")] NetDiff,
}

/// <summary>최종 차이 종류(§6.2 NetChange.change).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<NetChangeKind>))]
public enum NetChangeKind
{
    /// <summary>키가 새로 생김.</summary>
    [JsonStringEnumMemberName("INSERTED")] Inserted,
    /// <summary>키가 사라짐.</summary>
    [JsonStringEnumMemberName("DELETED")] Deleted,
    /// <summary>같은 키의 값이 바뀜.</summary>
    [JsonStringEnumMemberName("UPDATED")] Updated,
    /// <summary>키 없는 테이블: 모든 열 묶음의 개수가 줄어듦(E6).</summary>
    [JsonStringEnumMemberName("MULTISET_REMOVED")] MultisetRemoved,
    /// <summary>키 없는 테이블: 모든 열 묶음의 개수가 늘어남(E6).</summary>
    [JsonStringEnumMemberName("MULTISET_ADDED")] MultisetAdded,
}

/// <summary>FK 참조 동작(§6.2 foreignKey.action, §6.3 FK 간선 action). sys.foreign_keys *_referential_action_desc 값과 같다(NO_ACTION은 원인이 아니므로 없음).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<FkAction>))]
public enum FkAction
{
    /// <summary>CASCADE.</summary>
    [JsonStringEnumMemberName("CASCADE")] Cascade,
    /// <summary>SET_NULL.</summary>
    [JsonStringEnumMemberName("SET_NULL")] SetNull,
    /// <summary>SET_DEFAULT.</summary>
    [JsonStringEnumMemberName("SET_DEFAULT")] SetDefault,
}

/// <summary>FK 동작을 일으킨 부모 연산(§6.2 foreignKey.event).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<FkEvent>))]
public enum FkEvent
{
    /// <summary>부모 DELETE.</summary>
    [JsonStringEnumMemberName("DELETE")] Delete,
    /// <summary>부모 키를 바꾸는 UPDATE.</summary>
    [JsonStringEnumMemberName("UPDATE")] Update,
}

/// <summary>그래프 간선 종류(§6.3).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<EdgeKind>))]
public enum EdgeKind
{
    /// <summary>문장 → 묶음: 직접 대상 변경.</summary>
    [JsonStringEnumMemberName("DIRECT")] Direct,
    /// <summary>부모 묶음 → 자식 묶음: FK 참조 동작.</summary>
    [JsonStringEnumMemberName("FK")] Fk,
    /// <summary>묶음 → 트리거: 발동(카탈로그 기반 추론).</summary>
    [JsonStringEnumMemberName("FIRES")] Fires,
    /// <summary>문장 → 트리거: INSTEAD OF가 원래 동작을 대체.</summary>
    [JsonStringEnumMemberName("REPLACES")] Replaces,
    /// <summary>트리거 → 묶음: 트리거 실행 중 생긴 변경.</summary>
    [JsonStringEnumMemberName("WRITES")] Writes,
    /// <summary>트리거·묶음 → 잔차: 설명되지 않은 최종 변경의 후보 원인.</summary>
    [JsonStringEnumMemberName("RESIDUAL")] Residual,
    /// <summary>문장 → 오류: 실행 실패·거부.</summary>
    [JsonStringEnumMemberName("BLOCKED")] Blocked,
}

/// <summary>진단 심각도(§6.4 Diagnostic.severity). error는 거부, warning은 실행은 함(D5).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<Severity>))]
public enum Severity
{
    /// <summary>거부(실행하지 않음).</summary>
    [JsonStringEnumMemberName("error")] Error,
    /// <summary>경고(실행함).</summary>
    [JsonStringEnumMemberName("warning")] Warning,
}

/// <summary>샌드박스 상태(§6.4 SandboxStatus.state).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SandboxState>))]
public enum SandboxState
{
    /// <summary>기준선 구축 중.</summary>
    [JsonStringEnumMemberName("building")] Building,
    /// <summary>사용 가능(지문 불일치면 미리보기는 막힘, §5.1 6).</summary>
    [JsonStringEnumMemberName("ready")] Ready,
    /// <summary>오염(롤백 뒤 남은 변경, §5.2 4). 다음 미리보기 전에 재구축.</summary>
    [JsonStringEnumMemberName("dirty")] Dirty,
    /// <summary>오류.</summary>
    [JsonStringEnumMemberName("error")] Error,
}

/// <summary>행 페이지 보기(§6.4 GET rows view).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<RowView>))]
public enum RowView
{
    /// <summary>발동 보기: rows는 RowChange[].</summary>
    [JsonStringEnumMemberName("event")] Event,
    /// <summary>최종 차이 보기: rows는 NetChange[].</summary>
    [JsonStringEnumMemberName("net")] Net,
}

/// <summary>감사 행 이미지 종류(§6.2 AuditRowImage.imageKind, 저장 값 'DELETED'·'INSERTED').</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ImageKind>))]
public enum ImageKind
{
    /// <summary>deleted 가상 테이블 이미지.</summary>
    [JsonStringEnumMemberName("DELETED")] Deleted,
    /// <summary>inserted 가상 테이블 이미지.</summary>
    [JsonStringEnumMemberName("INSERTED")] Inserted,
}
