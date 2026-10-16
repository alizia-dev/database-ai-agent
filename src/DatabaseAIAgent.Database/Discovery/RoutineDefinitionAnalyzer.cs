using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using DatabaseAIAgent.Domain.Databases;

namespace DatabaseAIAgent.Database.Discovery;

/// <summary>
/// Static analysis of a routine's source to support READ/WRITE classification of AI tools. Deliberately conservative:
/// anything that might write (including calls to other routines and dynamic SQL) is flagged, and an unreadable
/// definition yields <see cref="RoutineBehavior.Unknown"/>. Writes to temporary tables / table variables are ignored.
/// The source text is not stored; only <see cref="Hash"/> is, to detect changes.
/// </summary>
public static partial class RoutineDefinitionAnalyzer
{
    public static RoutineBehavior Analyze(DatabaseProviderKind provider, string? definition, string? declaredDataAccess)
    {
        var declaredModifies = string.Equals(declaredDataAccess, "MODIFIES SQL DATA", StringComparison.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(definition))
        {
            return RoutineBehavior.Unknown with { ModifiesData = declaredModifies };
        }

        var code = Normalize(definition);
        return new RoutineBehavior(
            DefinitionAvailable: true,
            ModifiesData: declaredModifies || Writes().IsMatch(code),
            DeletesData: Deletes().IsMatch(code),
            ChangesSchema: Ddl().IsMatch(code),
            UsesDynamicSql: provider switch
            {
                DatabaseProviderKind.SqlServer => SqlServerDynamic().IsMatch(code),
                DatabaseProviderKind.PostgreSql => PostgresDynamic().IsMatch(code),
                _ => MySqlDynamic().IsMatch(code),
            },
            CallsOtherRoutines: provider switch
            {
                DatabaseProviderKind.SqlServer => SqlServerCalls().IsMatch(code),
                DatabaseProviderKind.PostgreSql => PostgresCalls().IsMatch(code),
                _ => MySqlCalls().IsMatch(code),
            });
    }

    public static string? Hash(string? definition) =>
        string.IsNullOrEmpty(definition) ? null : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(definition)));

    /// <summary>
    /// T-SQL does not expose parameter defaults in the catalog; read them from the header (best effort):
    /// <c>@name type = value</c> before the body's <c>AS</c>.
    /// </summary>
    public static IReadOnlySet<string> SqlServerParametersWithDefaults(string? definition)
    {
        if (string.IsNullOrEmpty(definition))
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        var code = StripCommentsAndStrings(definition);
        var header = HeaderEnd().Match(code) is { Success: true } end ? code[..end.Index] : code;
        return ParameterDefault().Matches(header).Select(m => m.Groups["name"].Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Upper-cased code without comments, string literals, quoted identifiers or the CREATE header.</summary>
    internal static string Normalize(string definition)
    {
        var code = StripCommentsAndStrings(definition).ToUpperInvariant();
        code = QuotedIdentifier().Replace(code, " IDENT ");
        return RoutineHeader().Replace(code, " ", 1);
    }

    private static string StripCommentsAndStrings(string text)
    {
        var result = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '-' && i + 1 < text.Length && text[i + 1] == '-')
            {
                while (i < text.Length && text[i] != '\n')
                {
                    i++;
                }

                result.Append('\n');
            }
            else if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < text.Length && !(text[i] == '*' && text[i + 1] == '/'))
                {
                    i++;
                }

                i++;
                result.Append(' ');
            }
            else if (c == '\'')
            {
                i++;
                while (i < text.Length)
                {
                    if (text[i] == '\'' && i + 1 < text.Length && text[i + 1] == '\'')
                    {
                        i += 2;
                        continue;
                    }

                    if (text[i] == '\'')
                    {
                        break;
                    }

                    i++;
                }

                result.Append("''");
            }
            else
            {
                result.Append(c);
            }
        }

        return result.ToString();
    }

    [GeneratedRegex(@"\[[^\]]*\]|""[^""]*""|`[^`]*`", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex QuotedIdentifier();

    [GeneratedRegex(@"^\s*(CREATE|ALTER)\s+(OR\s+(ALTER|REPLACE)\s+)?(DEFINER\s*=\s*\S+\s+)?(PROC|PROCEDURE|FUNCTION)\b", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex RoutineHeader();

    // INSERT/UPDATE/MERGE/REPLACE INTO a real table (not #temp or @table variables); not SELECT ... FOR UPDATE.
    // Atomic groups stop the optional INTO/FROM/TABLE keyword from being re-read as the target name.
    [GeneratedRegex(@"\bINSERT\s+(?>(INTO\s+)?)(?![#@])\w|(?<!FOR\s+)\bUPDATE\s+(?![#@])(?!STATISTICS\b)\w|\bMERGE\s+(?>(INTO\s+)?)(?![#@])\w|\bREPLACE\s+INTO\b|\bUPDATE\s+STATISTICS\b|\bCOPY\s+\w+\s+FROM\b", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex Writes();

    [GeneratedRegex(@"\bDELETE\s+(?>(FROM\s+)?)(?![#@])\w|\bTRUNCATE\s+(?>(TABLE\s+)?)(?![#@])\w", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex Deletes();

    [GeneratedRegex(@"\b(CREATE|ALTER|DROP)\s+(UNIQUE\s+|CLUSTERED\s+|NONCLUSTERED\s+)*(TABLE|VIEW|INDEX|PROC|PROCEDURE|FUNCTION|TRIGGER|SCHEMA|DATABASE|USER|LOGIN|ROLE|SEQUENCE|TYPE|EXTENSION)\s+(?!#)|\b(GRANT|REVOKE|DENY)\b", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex Ddl();

    // T-SQL dynamic SQL: EXEC('...'), EXEC @sql (but not "EXEC @rc = proc"), sp_executesql.
    [GeneratedRegex(@"\bEXEC(UTE)?\s*\(|\bEXEC(UTE)?\s+@(?>\w+)(?!\s*=)|\bSP_EXECUTESQL\b", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex SqlServerDynamic();

    // PL/pgSQL: every EXECUTE statement runs dynamic SQL.
    [GeneratedRegex(@"\bEXECUTE\b", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex PostgresDynamic();

    [GeneratedRegex(@"\bPREPARE\s+\w+\s+FROM\b|\bEXECUTE\s+\w+", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex MySqlDynamic();

    // T-SQL static calls: EXEC [@rc =] schema.proc (dynamic forms excluded).
    [GeneratedRegex(@"\bEXEC(UTE)?\s+(@\w+\s*=\s*)?(?!@|\(|SP_EXECUTESQL\b)[A-Z_][\w.]*", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex SqlServerCalls();

    // PostgreSQL: CALL proc / PERFORM func. Calls inside expressions cannot be detected statically; the declared
    // volatility (STABLE/IMMUTABLE functions cannot modify data, enforced by the engine) is the stronger signal.
    [GeneratedRegex(@"\bCALL\s+[A-Z_]|\bPERFORM\s+[A-Z_]", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex PostgresCalls();

    [GeneratedRegex(@"\bCALL\s+[A-Z_]", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex MySqlCalls();

    [GeneratedRegex(@"\)\s*AS\b|\bAS\s+(BEGIN|SET|SELECT|DECLARE|IF|WITH|RETURN|INSERT|UPDATE|DELETE|EXEC)\b|\bWITH\s+(EXECUTE|RECOMPILE|ENCRYPTION)\b", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 2000)]
    private static partial Regex HeaderEnd();

    [GeneratedRegex(@"(?<name>@(?>\w+))\s+[^,=()]+(\([^)]*\))?\s*=", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex ParameterDefault();
}
