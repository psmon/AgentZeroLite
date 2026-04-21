using System.Text;

namespace Agent.Common.Actors;

/// <summary>
/// Akka actor name sanitizer.
/// Akka는 actor 이름에 ASCII letter/digit + `"-_.*$+:@&=,!~';()` 만 허용.
/// 한글/비ASCII가 포함되면 InvalidActorNameException 발생 → 부모까지 restart.
/// 본 헬퍼는 금지 문자를 '_'로 대체하고 hash를 붙여 충돌을 방지한다.
/// 논리 ID(dict key)는 원본 그대로 유지, actor name 부분에만 사용.
/// </summary>
public static class ActorNameSanitizer
{
    public static string Safe(string name)
    {
        if (string.IsNullOrEmpty(name)) return "_";
        var sb = new StringBuilder(name.Length);
        bool hadReplacement = false;
        foreach (var ch in name)
        {
            if ((ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9')
                || ch == '-' || ch == '_' || ch == '.')
            {
                sb.Append(ch);
            }
            else
            {
                sb.Append('_');
                hadReplacement = true;
            }
        }
        if (hadReplacement)
            sb.Append('-').Append(name.GetHashCode().ToString("x8"));
        return sb.Length == 0 ? "_" : sb.ToString();
    }
}
