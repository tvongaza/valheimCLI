using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;

namespace valheimCLI
{
    /// <summary>
    /// cli_call: call a static method, or read a static field or property, of
    /// any loaded assembly (the game, a mod, the runtime) and print what it
    /// returns. A mod developer asks the mod itself a question from a script
    /// instead of adding a console command for each question.
    ///
    ///   cli_call [--limit N] [--assembly NAME] &lt;[Namespace.]Type.Member[.Member]&gt; [arg ...]
    ///
    /// The target may also be an instance member of a static member's value
    /// ("ZNet.instance.GetWorldName"), and an argument written @Type.Member
    /// passes that member's value ("@WorldGenerator.instance").
    ///
    /// Plain .NET, no Unity: the console command only hands this class the
    /// line and the loaded assemblies, so resolution, overload choice,
    /// argument conversion, invocation and output are all unit-tested.
    ///
    /// Non-public members are reachable on purpose. This is a debugging tool,
    /// the command is cheat-gated like the other cli_ commands, and the state
    /// a developer wants to see is usually private.
    ///
    /// Output, one fact per line:
    ///   VALUE &lt;text&gt;           the result (not printed for a void method)
    ///   ITEM &lt;i&gt; &lt;text&gt;        one per item when the result is a collection
    ///   MORE ...                  how many items --limit left out
    ///   OUT &lt;name&gt;=&lt;text&gt;      each out/ref parameter after the call
    ///   OK: CALL &lt;Type.Member&gt; kind=... type=... [via=...] [refN=...]
    /// or ERROR: code=&lt;code&gt; message=... followed by indented detail lines.
    /// </summary>
    public static class StaticMemberCall
    {
        public const string Usage = "Usage: cli_call [--limit N] [--assembly NAME] <[Namespace.]Type.Member[.Member]> [arg or @Type.Member ...]";
        public const int DefaultItemLimit = 50;
        public const int MaxItemLimit = 10000;
        /// <summary>Candidates or members listed under an error before the rest are only counted.</summary>
        public const int MaxListed = 30;

        private const BindingFlags StaticMembers =
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy;

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // ------------------------------------------------------------------
        // Request: the command line after "cli_call"
        // ------------------------------------------------------------------

        /// <summary>
        /// One argument as typed. A quoted token is text: it may hold spaces
        /// and is never null. A value token carries an object already in hand
        /// (the value of an @Type.Member reference) instead of text to read.
        /// </summary>
        public sealed class Token
        {
            public Token(string text, bool quoted)
            {
                Text = text;
                Quoted = quoted;
            }

            /// <param name="text">the reference as typed, for messages</param>
            /// <param name="value">the member's value, passed as it is</param>
            public static Token ForValue(string text, object? value) => new Token(text, false) { IsValue = true, Value = value };

            public string Text { get; }
            public bool Quoted { get; }
            public bool IsValue { get; private set; }
            public object? Value { get; private set; }
        }

        public sealed class Request
        {
            public Request(string path, List<Token> arguments, int itemLimit, string? assemblyPrefix = null)
            {
                Path = path;
                Arguments = arguments;
                ItemLimit = itemLimit;
                AssemblyPrefix = assemblyPrefix;
            }

            public string Path { get; }
            public List<Token> Arguments { get; }
            public int ItemLimit { get; }
            /// <summary>--assembly: only types from assemblies whose name starts with this (any case).</summary>
            public string? AssemblyPrefix { get; }
        }

        /// <summary>
        /// Split a line into tokens at whitespace. A token that starts with a
        /// double quote runs to the next unescaped double quote; inside it \"
        /// is a quote and \\ a backslash, any other backslash is literal (a
        /// Windows path needs no doubling).
        /// </summary>
        public static bool TryTokenize(string line, out List<Token> tokens, out string error)
        {
            tokens = new List<Token>();
            error = "";
            int i = 0;
            while (i < line.Length)
            {
                if (char.IsWhiteSpace(line[i]))
                {
                    i++;
                    continue;
                }
                if (line[i] != '"')
                {
                    int start = i;
                    while (i < line.Length && !char.IsWhiteSpace(line[i]))
                    {
                        i++;
                    }
                    tokens.Add(new Token(line.Substring(start, i - start), false));
                    continue;
                }
                StringBuilder text = new StringBuilder();
                int open = i++;
                bool closed = false;
                while (i < line.Length)
                {
                    char c = line[i];
                    if (c == '\\' && i + 1 < line.Length && (line[i + 1] == '"' || line[i + 1] == '\\'))
                    {
                        text.Append(line[i + 1]);
                        i += 2;
                        continue;
                    }
                    if (c == '"')
                    {
                        closed = true;
                        i++;
                        break;
                    }
                    text.Append(c);
                    i++;
                }
                if (!closed)
                {
                    error = "unterminated quote starting at column " + (open + 1).ToString(Inv);
                    return false;
                }
                if (i < line.Length && !char.IsWhiteSpace(line[i]))
                {
                    error = "text directly after the closing quote at column " + i.ToString(Inv) + "; separate arguments with a space";
                    return false;
                }
                tokens.Add(new Token(text.ToString(), true));
            }
            return true;
        }

        /// <summary>
        /// Options come before the member path, so an argument after it is
        /// never mistaken for one (a negative number, a string "--limit").
        /// </summary>
        public static bool TryParseRequest(string line, out Request? request, out string error)
        {
            request = null;
            if (!TryTokenize(line ?? "", out List<Token> tokens, out error))
            {
                return false;
            }
            int limit = DefaultItemLimit;
            string? assemblyPrefix = null;
            int next = 0;
            while (next < tokens.Count && !tokens[next].Quoted && tokens[next].Text.StartsWith("--", StringComparison.Ordinal))
            {
                string option = tokens[next].Text;
                if (option == "--limit")
                {
                    if (next + 1 >= tokens.Count || !int.TryParse(tokens[next + 1].Text, NumberStyles.None, Inv, out limit) || limit > MaxItemLimit)
                    {
                        error = "--limit takes a whole number from 0 to " + MaxItemLimit.ToString(Inv);
                        return false;
                    }
                }
                else if (option == "--assembly")
                {
                    if (next + 1 >= tokens.Count || tokens[next + 1].Text.Length == 0)
                    {
                        error = "--assembly takes the start of an assembly name";
                        return false;
                    }
                    assemblyPrefix = tokens[next + 1].Text;
                }
                else
                {
                    error = "unknown option " + option + "; the options are --limit N and --assembly NAME, before the member";
                    return false;
                }
                next += 2;
            }
            if (next >= tokens.Count || tokens[next].Quoted)
            {
                error = "";
                return false;
            }
            request = new Request(tokens[next].Text, tokens.Skip(next + 1).ToList(), limit, assemblyPrefix);
            return true;
        }

        // ------------------------------------------------------------------
        // Types: every loaded type, by its simple name
        // ------------------------------------------------------------------

        /// <summary>
        /// The loaded types, indexed by simple name so a lookup does not walk
        /// every assembly. Open generic types are left out (their static
        /// members cannot be called without type arguments), as are
        /// compiler-generated ones. An assembly that cannot list all of its
        /// types contributes the ones it can.
        /// </summary>
        public sealed class TypeIndex
        {
            private readonly Dictionary<string, List<Type>> _byName =
                new Dictionary<string, List<Type>>(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<Assembly, int> _loadOrder = new Dictionary<Assembly, int>();

            /// <param name="assemblies">in load order, as AppDomain.GetAssemblies lists them</param>
            public TypeIndex(IEnumerable<Assembly> assemblies)
            {
                int count = 0;
                foreach (Assembly assembly in assemblies)
                {
                    _loadOrder[assembly] = count;
                    count++;
                    foreach (Type type in LoadableTypes(assembly))
                    {
                        if (!IsIndexable(type))
                        {
                            continue;
                        }
                        if (!_byName.TryGetValue(type.Name, out List<Type>? list))
                        {
                            list = new List<Type>();
                            _byName[type.Name] = list;
                        }
                        list.Add(type);
                    }
                }
                AssemblyCount = count;
            }

            /// <summary>How many assemblies it was built from; a cache compares this to decide whether to rebuild.</summary>
            public int AssemblyCount { get; }

            /// <summary>An assembly's position in the load order (later is newer), or -1 if it was not indexed.</summary>
            public int LoadOrder(Assembly assembly) => _loadOrder.TryGetValue(assembly, out int order) ? order : -1;

            /// <summary>
            /// Types whose dotted full name (nested types joined with '.')
            /// is <paramref name="name"/> or ends with "." + name.
            /// </summary>
            public List<Type> Find(string name, bool ignoreCase = false)
            {
                StringComparison comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                int dot = name.LastIndexOf('.');
                string simple = dot >= 0 ? name.Substring(dot + 1) : name;
                if (!_byName.TryGetValue(simple, out List<Type>? list))
                {
                    return new List<Type>();
                }
                return list.Where(t =>
                {
                    string dotted = DottedName(t);
                    return string.Equals(dotted, name, comparison) ||
                        (dotted.Length > name.Length && dotted[dotted.Length - name.Length - 1] == '.' &&
                         dotted.EndsWith(name, comparison));
                }).ToList();
            }

            private static bool IsIndexable(Type type)
            {
                try
                {
                    return !type.ContainsGenericParameters && type.FullName != null && type.Name.IndexOf('<') < 0;
                }
                catch (Exception)
                {
                    // A type whose dependencies are missing can throw on
                    // inspection; it cannot be called either.
                    return false;
                }
            }

            private static IEnumerable<Type> LoadableTypes(Assembly assembly)
            {
                try
                {
                    return assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    return ex.Types.Where(t => t != null).Select(t => t!);
                }
                catch (Exception)
                {
                    return Enumerable.Empty<Type>();
                }
            }
        }

        /// <summary>
        /// Reuses one index while the set of loaded assemblies stays the same
        /// size, and rebuilds it when a mod loads another assembly. Assemblies
        /// are never unloaded from the game's domain, so the count only grows.
        /// </summary>
        public sealed class TypeIndexCache
        {
            private TypeIndex? _index;

            public int Builds { get; private set; }

            public TypeIndex For(Assembly[] assemblies)
            {
                if (_index == null || _index.AssemblyCount != assemblies.Length)
                {
                    _index = new TypeIndex(assemblies);
                    Builds++;
                }
                return _index;
            }
        }

        public static string DottedName(Type t) => (t.FullName ?? t.Name).Replace('+', '.');

        // ------------------------------------------------------------------
        // Resolution: path -> one type and its members of that name
        // ------------------------------------------------------------------

        public sealed class Failure
        {
            public Failure(string code, string message, List<string>? details = null)
            {
                Code = code;
                Message = message;
                Details = details ?? new List<string>();
            }

            public string Code { get; }
            public string Message { get; }
            public List<string> Details { get; }

            public IEnumerable<string> Lines()
            {
                yield return "ERROR: code=" + Code + " message=" + Message;
                foreach (string detail in Details)
                {
                    yield return "  " + detail;
                }
            }
        }

        public sealed class Resolution
        {
            public Resolution(Type type, string memberName, MemberInfo[] members, Copies copies)
            {
                Type = type;
                MemberName = memberName;
                Members = members;
                Copies = copies;
            }

            public Type Type { get; }
            public string MemberName { get; }
            public MemberInfo[] Members { get; }
            /// <summary>Which copy of the type was chosen when several assemblies hold the same full name.</summary>
            public Copies Copies { get; }
        }

        /// <summary>
        /// One full type name and the copy of it that is called. A mod that is
        /// reloaded in place (a script engine, a hot-reload tool) loads a new
        /// assembly under a new name, and the runtime never unloads the old
        /// one, so the same full name is then defined once per reload. Those
        /// are copies of one type, not a choice for the user to make: the live
        /// copy is used, which is the one whose assembly holds a running plugin
        /// or, when no copy does, the one loaded last.
        /// </summary>
        public sealed class Copies
        {
            public Copies(Type chosen, int stale, string chosenBy)
            {
                Chosen = chosen;
                Stale = stale;
                ChosenBy = chosenBy;
            }

            public Type Chosen { get; }
            /// <summary>How many other copies were passed over.</summary>
            public int Stale { get; }
            /// <summary>"only", "live" (its assembly holds a running plugin) or "newest" (loaded last).</summary>
            public string ChosenBy { get; }
        }

        /// <summary>
        /// Group types by full name and choose one copy per name: among the
        /// copies whose assembly is live, else among all, the one loaded last.
        /// <paramref name="liveAssemblies"/> is asked only when a name has
        /// more than one copy.
        /// </summary>
        public static List<Copies> ChooseCopies(TypeIndex index, IEnumerable<Type> types, Func<ICollection<Assembly>>? liveAssemblies)
        {
            ICollection<Assembly>? live = null;
            List<Copies> chosen = new List<Copies>();
            foreach (IGrouping<string, Type> sameName in types.GroupBy(DottedName, StringComparer.Ordinal))
            {
                List<Type> copies = sameName.ToList();
                if (copies.Count == 1)
                {
                    chosen.Add(new Copies(copies[0], 0, "only"));
                    continue;
                }
                live ??= liveAssemblies?.Invoke() ?? new List<Assembly>();
                List<Type> liveCopies = copies.Where(t => live.Contains(t.Assembly)).ToList();
                List<Type> pool = liveCopies.Count > 0 ? liveCopies : copies;
                Type newest = pool.OrderByDescending(t => index.LoadOrder(t.Assembly)).First();
                chosen.Add(new Copies(newest, copies.Count - 1, liveCopies.Count > 0 ? "live" : "newest"));
            }
            return chosen;
        }

        private static string AssemblyName(Type t) => t.Assembly.GetName().Name ?? "";

        /// <summary>
        /// Find the one type that the path names and that has a static member
        /// of that name. The type part may be a full name or any trailing
        /// part of one ("Utils", "MyMod.Utils", "Outer.Nested"). A type whose
        /// full name is exactly the given name wins over types that only end
        /// with it, so "ZNet.x" means the game's ZNet even if some mod has a
        /// MyMod.ZNet. Otherwise more than one match is ambiguous and every
        /// candidate is listed, with its assembly, to be named in full.
        /// Names are case-sensitive; a near miss in case is suggested.
        /// Copies of one full name in several assemblies are not ambiguous;
        /// see <see cref="Copies"/>. <paramref name="assemblyPrefix"/> keeps
        /// only types from assemblies whose name starts with it (any case).
        /// </summary>
        public static bool TryResolve(TypeIndex index, string path, out Resolution? resolution, out Failure? failure,
            Func<ICollection<Assembly>>? liveAssemblies = null, string? assemblyPrefix = null)
        {
            resolution = null;
            failure = null;
            int dot = path.LastIndexOf('.');
            if (dot <= 0 || dot == path.Length - 1)
            {
                List<Type> asType = dot < 0 ? index.Find(path) : new List<Type>();
                failure = new Failure("bad_request", "name a member as Type.Member or Namespace.Type.Member, not '" + path + "'",
                    asType.Count > 0 ? new List<string> { "'" + path + "' is a type; add the member: " + path + ".<Member>" } : null);
                return false;
            }
            string typeName = path.Substring(0, dot);
            string memberName = path.Substring(dot + 1);

            List<Type> named = index.Find(typeName);
            if (assemblyPrefix != null)
            {
                List<Type> inAssembly = named.Where(t => AssemblyName(t).StartsWith(assemblyPrefix, StringComparison.OrdinalIgnoreCase)).ToList();
                if (named.Count > 0 && inAssembly.Count == 0)
                {
                    failure = new Failure("no_type", "no loaded type named " + typeName + " in an assembly whose name starts with " + assemblyPrefix,
                        named.Select(AssemblyName).Distinct().Take(MaxListed).Select(a => "found in: " + a).ToList());
                    return false;
                }
                named = inAssembly;
            }
            if (named.Count == 0)
            {
                List<string> details = new List<string>();
                if (index.Find(path).Count > 0)
                {
                    details.Add("'" + path + "' is a type; add the member: " + path + ".<Member>");
                }
                foreach (Type near in index.Find(typeName, ignoreCase: true).Take(5))
                {
                    string member = StaticMemberNames(near)
                        .FirstOrDefault(n => string.Equals(n, memberName, StringComparison.OrdinalIgnoreCase)) ?? memberName;
                    details.Add("did you mean " + DottedName(near) + "." + member + "?");
                }
                failure = new Failure("no_type", "no loaded type named " + typeName, details);
                return false;
            }

            List<Type> exact = named.Where(t => DottedName(t) == typeName).ToList();
            List<Type> withMember = exact.Where(t => HasStaticMember(t, memberName)).ToList();
            if (withMember.Count == 0)
            {
                withMember = named.Where(t => HasStaticMember(t, memberName)).ToList();
            }

            List<Copies> distinct = ChooseCopies(index, withMember, liveAssemblies);
            if (distinct.Count > 1)
            {
                List<string> details = distinct.Take(MaxListed)
                    .Select(c => "candidate: " + DottedName(c.Chosen) + "." + memberName + " (" + AssemblyName(c.Chosen) + ")" +
                                 (c.Stale > 0 ? " and " + c.Stale.ToString(Inv) + " older copies" : "")).ToList();
                AddOmitted(details, distinct.Count);
                failure = new Failure("ambiguous_type",
                    distinct.Count.ToString(Inv) + " loaded types named " + typeName + " have a static " + memberName + "; give more of the namespace",
                    details);
                return false;
            }
            if (distinct.Count == 0)
            {
                List<Type> types = exact.Count > 0 ? exact : named;
                failure = NoMember(ChooseCopies(index, types, liveAssemblies).Select(c => c.Chosen).ToList(), typeName, memberName);
                return false;
            }

            Copies copy = distinct[0];
            resolution = new Resolution(copy.Chosen, memberName, copy.Chosen.GetMember(memberName, StaticMembers), copy);
            return true;
        }

        private static bool HasStaticMember(Type t, string name)
        {
            try
            {
                return t.GetMember(name, MemberTypes.Field | MemberTypes.Method | MemberTypes.Property, StaticMembers).Length > 0;
            }
            catch (Exception)
            {
                // A same-named type in a mod whose dependencies are missing
                // must not stop the lookup of the one the user meant.
                return false;
            }
        }

        private static Failure NoMember(List<Type> types, string typeName, string memberName)
        {
            List<string> details = new List<string>();
            if (types.Count > 1)
            {
                details.AddRange(types.Take(MaxListed).Select(t => "type: " + DottedName(t) + " (" + AssemblyName(t) + ")"));
                AddOmitted(details, types.Count);
                return new Failure("no_member", "none of the " + types.Count.ToString(Inv) + " loaded types named " + typeName + " has a static member " + memberName, details);
            }
            Type type = types[0];
            List<string> names = StaticMemberNames(type);
            foreach (string near in names.Where(n => string.Equals(n, memberName, StringComparison.OrdinalIgnoreCase)))
            {
                details.Add("did you mean " + DottedName(type) + "." + near + "?");
            }
            if (names.Count > 0)
            {
                details.Add("static members: " + string.Join(" ", names.Take(MaxListed).ToArray()) +
                    (names.Count > MaxListed ? " ...(" + (names.Count - MaxListed).ToString(Inv) + " more)" : ""));
            }
            else
            {
                details.Add(DottedName(type) + " has no static fields, properties or methods");
            }
            return new Failure("no_member", DottedName(type) + " has no static field, property or method named " + memberName, details);
        }

        /// <summary>
        /// Callable and readable names, without property accessors,
        /// compiler-generated members and object's own Equals/ReferenceEquals
        /// (which every type inherits).
        /// </summary>
        public static List<string> StaticMemberNames(Type type)
        {
            MemberInfo[] members;
            try
            {
                members = type.GetMembers(StaticMembers);
            }
            catch (Exception)
            {
                return new List<string>();
            }
            return members
                .Where(m => (m is FieldInfo || m is PropertyInfo || (m is MethodInfo method && !method.IsSpecialName)) &&
                            m.DeclaringType != typeof(object) && m.Name.IndexOf('<') < 0)
                .Select(m => m.Name)
                .Distinct()
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();
        }

        private static void AddOmitted(List<string> details, int total)
        {
            if (total > MaxListed)
            {
                details.Add("... " + (total - MaxListed).ToString(Inv) + " more");
            }
        }

        // ------------------------------------------------------------------
        // Overloads
        // ------------------------------------------------------------------

        public sealed class Choice
        {
            public Choice(MethodInfo method, object?[] values)
            {
                Method = method;
                Values = values;
            }

            public MethodInfo Method { get; }
            /// <summary>One value per parameter, ready for Invoke: out parameters hold their default.</summary>
            public object?[] Values { get; }
        }

        private sealed class Candidate
        {
            public MethodInfo Method = null!;
            public object?[] Values = null!;
            public int Cost;
            public int DefaultsUsed;
        }

        /// <summary>
        /// Pick the overload the arguments fit best. An overload fits when the
        /// argument count lies between its required and its total input
        /// parameters (out parameters are not given; optional ones may be
        /// left off) and every argument converts to its parameter. Among the
        /// fits the lowest total conversion cost wins (see CallValues.Cost),
        /// then the one that leaves fewest optional parameters to their
        /// defaults. A tie is reported, not guessed.
        /// </summary>
        public static bool TrySelectOverload(IList<MethodInfo> methods, IList<Token> arguments, out Choice? choice, out Failure? failure)
        {
            choice = null;
            failure = null;
            // Generic methods need type arguments and pointer parameters need
            // memory; neither can be typed on a console line.
            List<MethodInfo> callable = methods
                .Where(m => !m.ContainsGenericParameters && !m.GetParameters().Any(p => ElementType(p).IsPointer)).ToList();
            if (callable.Count == 0)
            {
                failure = new Failure("not_callable",
                    Describe(methods[0]) + " is generic or takes a pointer; cli_call cannot supply type arguments or pointers",
                    methods.Select(m => "overload: " + Signature(m)).ToList());
                return false;
            }

            List<Candidate> fits = new List<Candidate>();
            List<string> conversionFailures = new List<string>();
            int arityMatches = 0;
            foreach (MethodInfo method in callable)
            {
                ParameterInfo[] parameters = method.GetParameters();
                List<ParameterInfo> inputs = parameters.Where(p => !IsOut(p)).ToList();
                int required = inputs.Count(p => !p.IsOptional);
                if (arguments.Count < required || arguments.Count > inputs.Count)
                {
                    continue;
                }
                arityMatches++;
                object?[] values = new object?[parameters.Length];
                int cost = 0;
                int given = 0;
                string? failed = null;
                for (int i = 0; i < parameters.Length; i++)
                {
                    ParameterInfo p = parameters[i];
                    Type type = ElementType(p);
                    if (IsOut(p))
                    {
                        values[i] = DefaultOf(type);
                        continue;
                    }
                    if (given >= arguments.Count)
                    {
                        values[i] = p.HasDefaultValue && !(p.DefaultValue is DBNull) ? p.DefaultValue : DefaultOf(type);
                        continue;
                    }
                    Token token = arguments[given++];
                    bool converted = token.IsValue
                        ? CallValues.TryConvertValue(token.Value, type, out object? value, out int argumentCost)
                        : CallValues.TryConvert(token.Text, token.Quoted, type, out value, out argumentCost);
                    if (!converted)
                    {
                        failed = (token.IsValue
                                ? "cannot pass " + token.Text + " (" + (token.Value == null ? "null" : CallValues.FriendlyName(token.Value.GetType())) + ")"
                                : "cannot read " + (token.Quoted ? CallValues.Quote(token.Text) : "'" + token.Text + "'")) +
                            " as " + CallValues.FriendlyName(type) + " for parameter " + p.Name + " of " + Signature(method);
                        break;
                    }
                    values[i] = value;
                    cost += argumentCost;
                }
                if (failed != null)
                {
                    conversionFailures.Add(failed);
                    continue;
                }
                fits.Add(new Candidate { Method = method, Values = values, Cost = cost, DefaultsUsed = inputs.Count - arguments.Count });
            }

            if (fits.Count == 0)
            {
                List<string> overloads = callable.Select(m => "overload: " + Signature(m)).ToList();
                if (arityMatches == 1 && conversionFailures.Count == 1)
                {
                    failure = new Failure("bad_argument", conversionFailures[0], callable.Count > 1 ? overloads : null);
                }
                else if (arityMatches == 0)
                {
                    failure = new Failure("no_overload",
                        "no overload of " + Describe(callable[0]) + " takes " + arguments.Count.ToString(Inv) + " argument(s)", overloads);
                }
                else
                {
                    failure = new Failure("no_overload",
                        "no overload of " + Describe(callable[0]) + " accepts these arguments",
                        conversionFailures.Concat(overloads).ToList());
                }
                return false;
            }

            List<Candidate> best = fits
                .GroupBy(c => (c.Cost, c.DefaultsUsed))
                .OrderBy(g => g.Key.Cost).ThenBy(g => g.Key.DefaultsUsed)
                .First().ToList();
            if (best.Count > 1)
            {
                failure = new Failure("ambiguous_overload",
                    best.Count.ToString(Inv) + " overloads of " + Describe(best[0].Method) + " fit these arguments equally well; quote a string argument or add a decimal point to a number to choose",
                    best.Select(c => "overload: " + Signature(c.Method)).ToList());
                return false;
            }
            choice = new Choice(best[0].Method, best[0].Values);
            return true;
        }

        private static bool IsOut(ParameterInfo p) => p.IsOut && p.ParameterType.IsByRef;

        /// <summary>out and ref parameters are printed after the call; in parameters are not.</summary>
        private static bool IsWrittenBack(ParameterInfo p) => p.ParameterType.IsByRef && !p.IsIn;

        private static Type ElementType(ParameterInfo p) =>
            p.ParameterType.IsByRef ? p.ParameterType.GetElementType()! : p.ParameterType;

        private static object? DefaultOf(Type t) => t.IsValueType ? Activator.CreateInstance(t) : null;

        private static string Describe(MethodInfo m) => DottedName(m.DeclaringType!) + "." + m.Name;

        /// <summary><c>Type.Name(int a, out string b, float c = 1)</c></summary>
        public static string Signature(MethodInfo m)
        {
            IEnumerable<string> parameters = m.GetParameters().Select(p =>
            {
                string modifier = IsOut(p) ? "out " : p.ParameterType.IsByRef ? (p.IsIn ? "in " : "ref ") : "";
                string text = modifier + CallValues.FriendlyName(p.ParameterType) + " " + p.Name;
                if (p.IsOptional && p.HasDefaultValue && !(p.DefaultValue is DBNull))
                {
                    text += " = " + CallValues.Format(p.DefaultValue, false);
                }
                return text;
            });
            return Describe(m) + "(" + string.Join(", ", parameters.ToArray()) + ")";
        }

        // ------------------------------------------------------------------
        // Targets: a static member, or a member of a static member's value
        // ------------------------------------------------------------------

        private const BindingFlags InstanceMembers = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        /// <summary>
        /// What a path names: the members called <see cref="MemberName"/> of
        /// <see cref="Type"/>, static ones when <see cref="Instance"/> is null,
        /// otherwise instance ones of that object, the value of <see cref="Via"/>.
        /// </summary>
        public sealed class Target
        {
            public Target(Type type, string memberName, MemberInfo[] members, object? instance, string? via, Copies copies)
            {
                Type = type;
                MemberName = memberName;
                Members = members;
                Instance = instance;
                Via = via;
                Copies = copies;
            }

            public Type Type { get; }
            public string MemberName { get; }
            public MemberInfo[] Members { get; }
            public object? Instance { get; }
            /// <summary>The path whose value the instance member belongs to; null for a static member.</summary>
            public string? Via { get; }
            /// <summary>The copy chosen for the static member the path starts from.</summary>
            public Copies Copies { get; }

            /// <summary>Type.Member for the OK line.</summary>
            public string Name => DottedName(Type) + "." + MemberName;

            /// <summary>The path as it resolved: Type.Member, or Via.Member for an instance member.</summary>
            public string Path => Via == null ? Name : Via + "." + MemberName;
        }

        /// <summary>
        /// Resolve a path. "Type.Member" is a static member (see TryResolve).
        /// When no static member has the path and it has another dot, the
        /// part before the last dot is read as a value (a static field,
        /// property or parameterless method, or itself such a chain) and the
        /// last part is an instance member of that value:
        /// "ZNet.instance.GetWorldName" calls GetWorldName on the object that
        /// ZNet.instance holds. A static member with the whole path wins over
        /// a chain, so what a path meant before never changes.
        /// </summary>
        public static bool TryResolveTarget(TypeIndex index, string path, out Target? target, out Failure? failure,
            Func<ICollection<Assembly>>? liveAssemblies = null, string? assemblyPrefix = null, Action<string, Exception>? onThrow = null)
        {
            target = null;
            if (TryResolve(index, path, out Resolution? resolution, out failure, liveAssemblies, assemblyPrefix))
            {
                target = new Target(resolution!.Type, resolution.MemberName, resolution.Members, null, null, resolution.Copies);
                return true;
            }
            int dot = path.LastIndexOf('.');
            bool chain = (failure!.Code == "no_type" || failure.Code == "no_member") &&
                         dot > 0 && dot < path.Length - 1 && path.LastIndexOf('.', dot - 1) > 0;
            if (!chain)
            {
                return false;
            }
            string head = path.Substring(0, dot);
            string memberName = path.Substring(dot + 1);
            if (!TryReadValue(index, head, out object? value, out Target? source, out Failure? headFailure,
                    liveAssemblies, assemblyPrefix, onThrow))
            {
                // A head that is a type name, or that names no type at all,
                // was never a chain: the static reading's error is the one
                // that helps ("Outer.Nested has no Foo", not "Outer has no
                // static Nested").
                if (headFailure!.Code != "no_type" && index.Find(head).Count == 0)
                {
                    failure = headFailure;
                }
                return false;
            }
            string via = source!.Path;
            if (value == null)
            {
                failure = new Failure("null_target", via + " is null; there is no object to use " + memberName + " on");
                return false;
            }
            Type type = value.GetType();
            MemberInfo[] members = InstanceMembersNamed(type, memberName);
            if (members.Length == 0)
            {
                failure = NoInstanceMember(type, via, memberName);
                return false;
            }
            target = new Target(type, memberName, members, value, via, source.Copies);
            return true;
        }

        /// <summary>
        /// Read a path as a value: a field, a property, or a method without
        /// parameters (static, or on a chain as in TryResolveTarget).
        /// <paramref name="source"/> is the target that was read.
        /// </summary>
        public static bool TryReadValue(TypeIndex index, string path, out object? value, out Target? source, out Failure? failure,
            Func<ICollection<Assembly>>? liveAssemblies = null, string? assemblyPrefix = null, Action<string, Exception>? onThrow = null)
        {
            value = null;
            if (!TryResolveTarget(index, path, out source, out failure, liveAssemblies, assemblyPrefix, onThrow))
            {
                return false;
            }
            Target target = source!;
            try
            {
                MethodInfo[] methods = target.Members.OfType<MethodInfo>().ToArray();
                if (methods.Length > 0)
                {
                    MethodInfo? bare = methods.FirstOrDefault(m => !m.ContainsGenericParameters && m.GetParameters().Length == 0);
                    if (bare == null)
                    {
                        failure = new Failure("not_callable", target.Path +
                            " takes arguments; as a value only a field, a property or a method without parameters can be read",
                            methods.Select(m => "overload: " + Signature(m)).ToList());
                        return false;
                    }
                    value = bare.Invoke(target.Instance, null);
                    return true;
                }
                FieldInfo? field = target.Members.OfType<FieldInfo>().FirstOrDefault();
                if (field != null)
                {
                    value = field.GetValue(target.Instance);
                    return true;
                }
                PropertyInfo property = target.Members.OfType<PropertyInfo>().First();
                if (property.GetGetMethod(true) == null)
                {
                    failure = new Failure("not_callable", target.Path + " has no getter");
                    return false;
                }
                value = property.GetValue(target.Instance, null);
                return true;
            }
            catch (Exception ex)
            {
                failure = CallFailure(target.Path, ex, onThrow);
                return false;
            }
        }

        /// <summary>
        /// Instance fields, properties (not indexers) and methods of that name,
        /// private ones of base classes included. A method overridden further
        /// down is listed once; a field or property hides one of a base class.
        /// </summary>
        private static MemberInfo[] InstanceMembersNamed(Type type, string name)
        {
            List<MemberInfo> found = new List<MemberInfo>();
            HashSet<MethodInfo> bases = new HashSet<MethodInfo>();
            bool haveValue = false;
            for (Type? t = type; t != null; t = t.BaseType)
            {
                foreach (MemberInfo member in t.GetMember(name, MemberTypes.Field | MemberTypes.Method | MemberTypes.Property,
                             InstanceMembers | BindingFlags.DeclaredOnly))
                {
                    if (member is MethodInfo method)
                    {
                        if (bases.Add(method.GetBaseDefinition()))
                        {
                            found.Add(method);
                        }
                    }
                    else if (!haveValue && !(member is PropertyInfo indexed && indexed.GetIndexParameters().Length > 0))
                    {
                        found.Add(member);
                        haveValue = true;
                    }
                }
            }
            return found.ToArray();
        }

        private static Failure NoInstanceMember(Type type, string via, string memberName)
        {
            // The value's own runtime type is loaded and complete, so unlike
            // the index's types it can be listed without guarding.
            List<string> names = new List<string>();
            for (Type? t = type; t != null && t != typeof(object); t = t.BaseType)
            {
                names.AddRange(t.GetMembers(InstanceMembers | BindingFlags.DeclaredOnly)
                    .Where(m => (m is FieldInfo || (m is PropertyInfo p && p.GetIndexParameters().Length == 0) ||
                                 (m is MethodInfo method && !method.IsSpecialName)) && m.Name.IndexOf('<') < 0)
                    .Select(m => m.Name));
            }
            names = names.Distinct().OrderBy(n => n, StringComparer.Ordinal).ToList();
            List<string> details = names.Where(n => string.Equals(n, memberName, StringComparison.OrdinalIgnoreCase))
                .Select(n => "did you mean " + via + "." + n + "?").ToList();
            if (names.Count > 0)
            {
                details.Add("instance members: " + string.Join(" ", names.Take(MaxListed).ToArray()) +
                    (names.Count > MaxListed ? " ...(" + (names.Count - MaxListed).ToString(Inv) + " more)" : ""));
            }
            return new Failure("no_member", via + " is a " + DottedName(type) + ", which has no instance field, property or method named " + memberName, details);
        }

        // ------------------------------------------------------------------
        // Run: the whole command
        // ------------------------------------------------------------------

        /// <summary>
        /// Run one cli_call line against the given types and write its output.
        /// <paramref name="onThrow"/> receives the full exception when the
        /// target throws, for the plugin to log with its stack trace; the
        /// console gets the type and message. <paramref name="liveAssemblies"/>
        /// lists the assemblies that hold a running plugin; it is asked only
        /// when a type has copies in several assemblies (see <see cref="Copies"/>).
        /// </summary>
        public static void Run(TypeIndex index, string line, Action<string> output, Action<string, Exception>? onThrow = null,
            Func<ICollection<Assembly>>? liveAssemblies = null)
        {
            if (!TryParseRequest(line, out Request? request, out string error))
            {
                if (error.Length > 0)
                {
                    output("ERROR: code=bad_request message=" + error);
                }
                output(Usage);
                return;
            }
            if (!TryResolveTarget(index, request!.Path, out Target? resolved, out Failure? failure, liveAssemblies, request.AssemblyPrefix, onThrow))
            {
                Emit(failure!, output);
                return;
            }
            Target target = resolved!;

            // @Type.Member arguments are read now, in order, and passed as the
            // objects they are; @@ stands for a literal leading @.
            List<Token> arguments = new List<Token>();
            StringBuilder references = new StringBuilder();
            for (int i = 0; i < request.Arguments.Count; i++)
            {
                Token token = request.Arguments[i];
                if (token.Quoted || !token.Text.StartsWith("@", StringComparison.Ordinal))
                {
                    arguments.Add(token);
                    continue;
                }
                if (token.Text.StartsWith("@@", StringComparison.Ordinal))
                {
                    arguments.Add(new Token(token.Text.Substring(1), false));
                    continue;
                }
                if (!TryReadValue(index, token.Text.Substring(1), out object? value, out Target? source, out Failure? argumentFailure,
                        liveAssemblies, null, onThrow))
                {
                    Emit(new Failure(argumentFailure!.Code, "argument " + (i + 1).ToString(Inv) + " (" + token.Text + "): " + argumentFailure.Message,
                        argumentFailure.Details), output);
                    return;
                }
                arguments.Add(Token.ForValue(token.Text, value));
                references.Append(" ref").Append((i + 1).ToString(Inv)).Append('=').Append(source!.Path);
            }

            string path = target.Path;
            object? instance = target.Instance;
            MethodInfo[] methods = target.Members.OfType<MethodInfo>().ToArray();
            FieldInfo? field = target.Members.OfType<FieldInfo>().FirstOrDefault();
            PropertyInfo? property = target.Members.OfType<PropertyInfo>().FirstOrDefault();

            if (methods.Length == 0 && arguments.Count > 0)
            {
                string kind = field != null ? "field" : "property";
                Emit(new Failure("bad_argument", path + " is a " + kind + "; it takes no arguments (cli_call reads it, it does not set it)"), output);
                return;
            }

            object? result;
            Type resultType;
            string header;
            Choice? choice = null;
            try
            {
                if (methods.Length > 0)
                {
                    if (!TrySelectOverload(methods, arguments, out choice, out failure))
                    {
                        Emit(failure!, output);
                        return;
                    }
                    resultType = choice!.Method.ReturnType;
                    header = "kind=method" + (methods.Length > 1 ? " overload=" + ParameterTypes(choice.Method) : "");
                    result = choice.Method.Invoke(instance, choice.Values);
                }
                else if (field != null)
                {
                    resultType = field.FieldType;
                    header = field.IsLiteral ? "kind=constant" : "kind=field";
                    result = field.GetValue(instance);
                }
                else
                {
                    if (property!.GetGetMethod(true) == null)
                    {
                        Emit(new Failure("not_callable", path + " has no getter"), output);
                        return;
                    }
                    resultType = property.PropertyType;
                    header = "kind=property";
                    result = property.GetValue(instance, null);
                }
            }
            catch (Exception ex)
            {
                Emit(CallFailure(path, ex, onThrow), output);
                return;
            }

            string summary = "";
            if (resultType != typeof(void))
            {
                if (CallValues.IsItemized(result))
                {
                    if (!TryWriteItems((IEnumerable)result!, request.ItemLimit, output, out summary, out Exception? walkError))
                    {
                        Threw(path + " (walking the result)", walkError!, output, onThrow);
                        return;
                    }
                }
                else
                {
                    output("VALUE " + CallValues.Format(result));
                }
            }
            if (choice != null)
            {
                ParameterInfo[] parameters = choice.Method.GetParameters();
                for (int i = 0; i < parameters.Length; i++)
                {
                    if (IsWrittenBack(parameters[i]))
                    {
                        output("OUT " + parameters[i].Name + "=" + CallValues.Format(choice.Values[i]));
                    }
                }
            }
            string via = target.Via != null ? " via=" + target.Via : "";
            Type copied = target.Copies.Chosen;
            string copies = target.Copies.Stale > 0
                ? " assembly=" + AssemblyName(copied) + " stale_copies=" + target.Copies.Stale.ToString(Inv) + " chosen=" + target.Copies.ChosenBy
                : "";
            output("OK: CALL " + target.Name + " " + header + " type=" + CallValues.FriendlyName(resultType) + summary + via + references + copies);
        }

        /// <summary>
        /// An exception from calling or reading a member. The target's own
        /// exception (unwrapped from reflection's wrapper) or a failed static
        /// constructor is call_threw; anything else means reflection could not
        /// make the call at all (call_failed).
        /// </summary>
        private static Failure CallFailure(string target, Exception ex, Action<string, Exception>? onThrow)
        {
            if (ex is TargetInvocationException && ex.InnerException != null)
            {
                ex = ex.InnerException;
            }
            else if (!(ex is TypeInitializationException))
            {
                onThrow?.Invoke(target, ex);
                return new Failure("call_failed", "could not call " + target + ": " + ex.GetType().FullName + ": " + OneLine(ex.Message));
            }
            onThrow?.Invoke(target, ex);
            return new Failure("call_threw", ThrewMessage(target, ex));
        }

        /// <summary>
        /// Item lines up to the limit. A collection knows its count; for any
        /// other sequence one extra item is read to learn whether more exist,
        /// never the whole of it (it may be endless).
        /// </summary>
        private static bool TryWriteItems(IEnumerable sequence, int limit, Action<string> output, out string summary, out Exception? error)
        {
            summary = "";
            error = null;
            bool counted = CallValues.TryCount(sequence, out int count);
            int shown = 0;
            bool more = false;
            List<string> lines = new List<string>();
            try
            {
                IEnumerator enumerator = sequence.GetEnumerator();
                try
                {
                    while (shown < limit && enumerator.MoveNext())
                    {
                        lines.Add("ITEM " + shown.ToString(Inv) + " " + CallValues.Format(enumerator.Current));
                        shown++;
                    }
                    if (!counted && shown == limit)
                    {
                        more = enumerator.MoveNext();
                    }
                }
                finally
                {
                    (enumerator as IDisposable)?.Dispose();
                }
            }
            catch (Exception ex)
            {
                error = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
                return false;
            }
            foreach (string line in lines)
            {
                output(line);
            }
            if (counted)
            {
                if (count > shown)
                {
                    output("MORE " + (count - shown).ToString(Inv) + " of " + count.ToString(Inv) + " items not shown; raise --limit (now " + limit.ToString(Inv) + ")");
                }
                summary = " items=" + count.ToString(Inv) + " shown=" + shown.ToString(Inv);
            }
            else
            {
                if (more)
                {
                    output("MORE items not shown (a sequence, not a collection: its length is unknown); raise --limit (now " + limit.ToString(Inv) + ")");
                }
                summary = " items=" + (more ? "?" : shown.ToString(Inv)) + " shown=" + shown.ToString(Inv);
            }
            return true;
        }

        /// <summary>The chosen overload in the OK line, without spaces: <c>(int,out:string)</c>.</summary>
        private static string ParameterTypes(MethodInfo m) =>
            "(" + string.Join(",", m.GetParameters().Select(p =>
                (IsOut(p) ? "out:" : p.ParameterType.IsByRef ? (p.IsIn ? "in:" : "ref:") : "") +
                CallValues.FriendlyName(p.ParameterType).Replace(" ", "")).ToArray()) + ")";

        /// <summary>
        /// The target's own exception, type and message. A failed static
        /// constructor says only that it failed, so its cause is added.
        /// </summary>
        private static void Threw(string target, Exception ex, Action<string> output, Action<string, Exception>? onThrow)
        {
            onThrow?.Invoke(target, ex);
            output("ERROR: code=call_threw message=" + ThrewMessage(target, ex));
        }

        private static string ThrewMessage(string target, Exception ex)
        {
            string message = OneLine(ex.Message);
            if (ex is TypeInitializationException && ex.InnerException != null)
            {
                message += " (" + ex.InnerException.GetType().FullName + ": " + OneLine(ex.InnerException.Message) + ")";
            }
            return target + " threw " + ex.GetType().FullName + ": " + message;
        }

        private static string OneLine(string text) => text.Replace("\r", " ").Replace("\n", " ").Trim();

        private static void Emit(Failure failure, Action<string> output)
        {
            foreach (string line in failure.Lines())
            {
                output(line);
            }
        }
    }
}
