using System.Reflection;
using Microsoft.Data.SqlClient;

namespace DbRipple.Apply.Tests;

/// <summary>
/// 단위 테스트용 SqlException(오류 번호만 의미 있음). SqlClient는 공개 생성자가 없어 내부 생성자를 매개변수 이름으로 채워 만든다.
/// 내부 모양이 바뀌면 여기서 실패한다(제품 코드는 쓰지 않는다).
/// </summary>
internal static class SqlErrors
{
    public static SqlException Make(int number, string message)
    {
        const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
        var ctor = typeof(SqlError).GetConstructors(Any).OrderByDescending(c => c.GetParameters().Length).First();
        var error = (SqlError)ctor.Invoke(ctor.GetParameters().Select(p => Arg(p, number, message)).ToArray());
        var errors = (SqlErrorCollection)Activator.CreateInstance(typeof(SqlErrorCollection), nonPublic: true)!;
        typeof(SqlErrorCollection).GetMethod("Add", Any, [typeof(SqlError)])!.Invoke(errors, [error]);
        var create = typeof(SqlException).GetMethods(Any)
            .Where(m => m.Name == "CreateException" && m.GetParameters() is [{ ParameterType: var t0 }, { ParameterType: var t1 }] && t0 == typeof(SqlErrorCollection) && t1 == typeof(string))
            .First();
        return (SqlException)create.Invoke(null, [errors, "17.0"])!;
    }

    static object? Arg(ParameterInfo p, int number, string message) => p.Name switch
    {
        "infoNumber" => number,
        "errorClass" => (byte)16,
        "errorMessage" => message,
        _ when p.ParameterType == typeof(string) => "",
        _ when p.ParameterType.IsValueType => Activator.CreateInstance(p.ParameterType),
        _ => null,
    };
}
