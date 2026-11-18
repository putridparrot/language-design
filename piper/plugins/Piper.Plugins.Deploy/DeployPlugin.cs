using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Piper.Plugins;
using Piper.Syntax;
using Piper.Text;

namespace Piper.Plugins.Deploy;

/// <summary>
/// <c>deploy</c>: how a program runs on Kubernetes, said once beside its code, from which <c>piper build</c>, <c>run</c>
/// and <c>test</c> write the manifest (checked in, as a shape is), so the YAML never drifts from the program or from the
/// other apps' and isn't written by hand.
/// <code>
/// deploy Stock to "k8s/stock.yaml" {
///     postgres "stock-db" database "stock";            // a Secret, a Service, a StatefulSet with a volume
///     role stock {                                     // a Deployment
///         replicas 3;
///         args "--host", pod.ip;                       // pod.ip, pod.name: from the downward API
///         port http 8080 public 80;                    // public: a LoadBalancer (on port 80 here)
///         port actors 8000;
///         health "/healthz" ready "/ready";            // liveness and readiness probes, on the http port
///         env Events__Provider = "postgres";
///         env Events__Connection = secret "stock-db" "connection";
///         stopping 30 seconds;                         // how long it has to stop (terminationGracePeriodSeconds)
///     }
/// }
/// </code>
/// </summary>
public sealed class DeployPlugin : IPiperPlugin
{
    public string Name => "deploy";

    public IReadOnlyDictionary<string, string> Packages { get; } = new Dictionary<string, string>();

    public IReadOnlyList<string> References => [];

    public IReadOnlyList<string> Providers => [];

    public IReadOnlyList<PluginKeyword> Keywords { get; } =
    [
        new DeclarationKeyword("deploy", Deploy)
        {
            Description = "How the program runs on Kubernetes: its roles (each a Deployment), their replicas, ports, arguments, settings and " +
                          "probes, and any database. `piper build`, `run` and `test` write the manifest beside it (`Name.k8s.yaml`, or `to \"path\"`), " +
                          "to check in; the editor never does.",
            Snippet = "deploy ${1:App} {\n\trole ${2:app} {\n\t\treplicas ${3:2};\n\t\tport http ${4:8080} public;\n\t\thealth \"/healthz\" ready \"/ready\";\n\t}\n}",
            Words = new Dictionary<string, string>
            {
                ["to"] = "Where the manifest is written, relative to this file: `to \"k8s/app.yaml\"`. Without it, `Name.k8s.yaml` beside it.",
                ["image"] = "The container image the roles run: `image \"myregistry.azurecr.io/stock:1\"`. Without it, `REGISTRY/name:1`, to replace.",
                ["role"] = "A Deployment: pods running the program, each with the same arguments and settings.",
                ["replicas"] = "How many pods the role runs.",
                ["args"] = "What the program's Main is given: strings, numbers, `pod.ip` and `pod.name` (the pod's own, from the downward API).",
                ["port"] = "A port the program listens on, named: `port http 8080;`. `public` puts it behind a LoadBalancer (named as the app, `public 80` on port 80); `service` makes it a ClusterIP service named as the role, for other pods.",
                ["health"] = "Probes, on the `http` port: `health \"/healthz\"` is liveness (restarted if it fails); `ready \"/ready\"` is readiness (sent nothing until it passes).",
                ["env"] = "A setting, as an environment variable: `env Events__Provider = \"postgres\";`, `= secret \"name\" \"key\"`, `= pod.ip`.",
                ["stopping"] = "How long a pod has to stop once asked (handing work over) before it's killed: `stopping 30 seconds;`.",
                ["resources"] = "What each pod asks for, and its limits: `resources cpu \"500m\" memory \"128Mi\" limit cpu \"1\" memory \"256Mi\";`.",
                ["recreate"] = "Never two pods at once: an update stops the old before starting the new (for a role holding state, with one replica).",
                ["volume"] = "Storage that outlives the pod: `volume data \"/data\" size \"1Gi\";` makes a PersistentVolumeClaim (`app-role-data`) mounted at the path. One pod at a time can mount it, so the role has one replica (and `recreate`).",
                ["postgres"] = "A PostgreSQL for the roles: `postgres \"stock-db\" database \"stock\";` makes its Secret (`password`, and `connection`, " +
                               "for `env … = secret \"stock-db\" \"connection\"`), its Service and a StatefulSet with a 1Gi volume. Change the Secret's password.",
            },
        },
    ];

    private sealed record Value(string? Text, string? Field, string? Secret, string? Key);

    private sealed record Port(string Name, int Number, int? Public, bool Service);

    private sealed record Role(Token Name)
    {
        public int Replicas { get; set; } = 1;
        public List<Value> Args { get; } = [];
        public List<Port> Ports { get; } = [];
        public string? Health { get; set; }
        public string? Ready { get; set; }
        public List<(string Name, Value Value)> Env { get; } = [];
        public int? Stopping { get; set; }
        public bool Recreate { get; set; }
        public (string? Cpu, string? Memory, string? CpuLimit, string? MemoryLimit)? Resources { get; set; }
        public List<(string Name, string Path, string Size)> Volumes { get; } = [];
    }

    private sealed record Deployment(Token Name, string Target, string? Image, List<Role> Roles, List<(string Name, string Database)> Databases);

    /// <summary>
    /// deploy = "deploy" IDENT [ "to" STRING ] "{" { [ "image" STRING ";" ] | postgres | role } "}" ;
    /// </summary>
    private static IEnumerable<DeclarationSyntax> Deploy(PluginParser p)
    {
        var name = p.Expect(TokenKind.Identifier);
        var target = $"{name.Text}.k8s.yaml";
        if (p.AtWord("to"))
        {
            p.ReadWord();
            target = Text(p) ?? target;
        }
        p.Expect(TokenKind.OpenBrace);
        string? image = null;
        var roles = new List<Role>();
        var databases = new List<(string, string)>();
        while (p.Current.Kind is not (TokenKind.CloseBrace or TokenKind.EndOfFile))
        {
            if (p.AtWord("image"))
            {
                p.ReadWord();
                image = Text(p);
                p.Expect(TokenKind.Semicolon);
            }
            else if (p.AtWord("postgres"))
            {
                p.ReadWord();
                var server = Text(p) ?? "db";
                p.ExpectWord("database");
                var database = Text(p) ?? "app";
                p.Expect(TokenKind.Semicolon);
                databases.Add((server, database));
            }
            else if (p.AtWord("role"))
            {
                p.ReadWord();
                var role = new Role(p.Expect(TokenKind.Identifier));
                if (roles.Any(r => r.Name.Text == role.Name.Text))
                    p.Report(role.Name.Span, $"There's already a role '{role.Name.Text}'.");
                ParseRole(p, role);
                if (role.Volumes.Count > 0 && role.Replicas > 1)
                    p.Report(role.Name.Span, $"{role.Name.Text} has a volume, which one pod at a time can mount, but {role.Replicas} replicas; give it one, and `recreate`.");
                roles.Add(role);
            }
            else
            {
                p.Report(p.Current.Span, "Expected 'role', 'postgres' or 'image'.");
                Skip(p);
            }
        }
        p.Expect(TokenKind.CloseBrace);
        if (roles.Count(r => r.Ports.Any(port => port.Public is not null)) > 1)
            p.Report(name.Span, "Only one role can have public ports: they share the LoadBalancer named after the app.");
        p.Data = new Deployment(name, target, image, roles, databases);
        return [];
    }

    private static void ParseRole(PluginParser p, Role role)
    {
        p.Expect(TokenKind.OpenBrace);
        while (p.Current.Kind is not (TokenKind.CloseBrace or TokenKind.EndOfFile))
        {
            var word = p.Current;
            switch (word.Text)
            {
                case "replicas":
                    p.ReadWord();
                    role.Replicas = Number(p) ?? 1;
                    break;
                case "args":
                    p.ReadWord();
                    do
                    {
                        if (role.Args.Count > 0)
                            p.NextToken();
                        if (ValueOf(p) is { } argument)
                            role.Args.Add(argument);
                    }
                    while (p.Current.Kind == TokenKind.Comma);
                    break;
                case "port":
                {
                    p.ReadWord();
                    var portName = p.Expect(TokenKind.Identifier).Text;
                    var number = Number(p) ?? 0;
                    int? exposed = null;
                    var service = false;
                    while (p.AtWord("public") || p.AtWord("service"))
                    {
                        if (p.ReadWord().Text == "service")
                            service = true;
                        else
                            exposed = p.Current.Kind == TokenKind.IntegerLiteral ? Number(p) : number;
                    }
                    role.Ports.Add(new Port(portName, number, exposed, service));
                    break;
                }
                case "health":
                    p.ReadWord();
                    role.Health = Text(p);
                    if (p.AtWord("ready"))
                    {
                        p.ReadWord();
                        role.Ready = Text(p);
                    }
                    break;
                case "env":
                {
                    p.ReadWord();
                    var variable = p.Expect(TokenKind.Identifier).Text;
                    p.Expect(TokenKind.Equals);
                    if (ValueOf(p) is { } value)
                        role.Env.Add((variable, value));
                    break;
                }
                case "stopping":
                {
                    p.ReadWord();
                    var count = Number(p) ?? 30;
                    var unit = p.Current.Kind == TokenKind.Identifier ? p.ReadWord().Text : "seconds";
                    role.Stopping = unit.StartsWith("minute", StringComparison.Ordinal) ? count * 60 : count;
                    break;
                }
                case "recreate":
                    p.ReadWord();
                    role.Recreate = true;
                    break;
                case "volume":
                {
                    // `volume data "/data" size "1Gi";`: a PersistentVolumeClaim, mounted at the path.
                    p.ReadWord();
                    var volume = p.Expect(TokenKind.Identifier).Text;
                    var mount = Text(p) ?? "/data";
                    p.ExpectWord("size");
                    var size = Text(p) ?? "1Gi";
                    role.Volumes.Add((volume, mount, size));
                    break;
                }
                case "resources":
                {
                    p.ReadWord();
                    string? cpu = null, memory = null, cpuLimit = null, memoryLimit = null;
                    var limit = false;
                    while (p.AtWord("cpu") || p.AtWord("memory") || p.AtWord("limit"))
                    {
                        var what = p.ReadWord().Text;
                        if (what == "limit")
                        {
                            limit = true;
                            continue;
                        }
                        var amount = Text(p);
                        if (what == "cpu" && limit) cpuLimit = amount;
                        else if (what == "cpu") cpu = amount;
                        else if (limit) memoryLimit = amount;
                        else memory = amount;
                    }
                    role.Resources = (cpu, memory, cpuLimit, memoryLimit);
                    break;
                }
                default:
                    p.Report(word.Span, $"'{word.Text}' isn't part of a role; expected 'replicas', 'args', 'port', 'health', 'env', 'stopping', 'resources', 'volume' or 'recreate'.");
                    Skip(p);
                    continue;
            }
            p.Expect(TokenKind.Semicolon);
        }
        p.Expect(TokenKind.CloseBrace);
    }

    /// <summary>A value: a string or number, <c>pod.ip</c> or <c>pod.name</c>, or <c>secret "name" "key"</c>.</summary>
    private static Value? ValueOf(PluginParser p)
    {
        if (p.AtWord("secret"))
        {
            p.ReadWord();
            var secret = Text(p);
            var key = Text(p);
            return secret is null || key is null ? null : new Value(null, null, secret, key);
        }
        var start = p.Current;
        var expression = p.ParseExpression();
        switch (expression)
        {
            case LiteralExpressionSyntax { Value: string text }:
                return new Value(text, null, null, null);
            case LiteralExpressionSyntax { Value: int or long } number:
                return new Value(Convert.ToString(number.Value, CultureInfo.InvariantCulture), null, null, null);
            case MemberAccessExpressionSyntax { Target: NameExpressionSyntax { Identifier.Text: "pod" }, Member.Text: var field }
                when field is "ip" or "name":
                return new Value(null, field, null, null);
            default:
                p.Report(expression.Span, "Expected a string, a number, `pod.ip`, `pod.name`, or `secret \"name\" \"key\"`.");
                return null;
        }
    }

    private static string? Text(PluginParser p)
    {
        var token = p.Current;
        if (token.Kind == TokenKind.StringLiteral && token.Value is string text)
        {
            p.NextToken();
            return text;
        }
        p.Report(token.Span, "Expected a string, such as \"stock\".");
        return null;
    }

    private static int? Number(PluginParser p)
    {
        var token = p.Current;
        if (token.Kind == TokenKind.IntegerLiteral && token.Value is int number)
        {
            p.NextToken();
            return number;
        }
        p.Report(token.Span, "Expected a whole number.");
        return null;
    }

    private static void Skip(PluginParser p)
    {
        while (p.Current.Kind is not (TokenKind.Semicolon or TokenKind.CloseBrace or TokenKind.EndOfFile))
            p.NextToken();
        if (p.Current.Kind == TokenKind.Semicolon)
            p.NextToken();
    }

    /// <summary>
    /// Writes each <c>deploy</c>'s manifest where it says, when the program is built, run or tested (not when the editor
    /// checks it), if it changed.
    /// </summary>
    public void Analyze(PluginAnalysis analysis)
    {
        foreach (var expansion in analysis.Expansions)
        {
            if (expansion.Data is not Deployment deployment || analysis.ShapeMode == WireShapeMode.Check)
                continue;
            if (!File.Exists(expansion.File.FileName))
                continue;
            var folder = Path.GetDirectoryName(Path.GetFullPath(expansion.File.FileName))!;
            var path = Path.GetFullPath(Path.Combine(folder, deployment.Target));
            var yaml = Manifest(deployment, Path.GetFileName(expansion.File.FileName));
            if (File.Exists(path) && File.ReadAllText(path).ReplaceLineEndings("\n") == yaml)
                continue;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, yaml);
        }
    }

    /// <summary>The Kubernetes manifest for <paramref name="deployment"/>, declared in <paramref name="source"/>.</summary>
    private static string Manifest(Deployment deployment, string source)
    {
        var app = Kebab(deployment.Name.Text);
        var image = deployment.Image ?? $"REGISTRY/{app}:1";
        var documents = new List<string>();

        foreach (var (server, database) in deployment.Databases)
            documents.AddRange(Postgres(server, database));

        foreach (var role in deployment.Roles)
        {
            var name = Kebab(role.Name.Text) == app ? app : $"{app}-{Kebab(role.Name.Text)}";
            var labels = $"app: {app}\nrole: {Kebab(role.Name.Text)}";
            if (role.Ports.Where(p => p.Public is not null).ToList() is { Count: > 0 } exposed)
                documents.Add(Service(app, labels, "LoadBalancer", [.. exposed.Select(p => (p.Name, p.Public!.Value, p.Name))]));
            if (role.Ports.Where(p => p.Service).ToList() is { Count: > 0 } internals)
                documents.Add(Service(name, labels, null, [.. internals.Select(p => (p.Name, p.Number, p.Name))]));
            foreach (var (volume, _, size) in role.Volumes)
                documents.Add($"apiVersion: v1\nkind: PersistentVolumeClaim\nmetadata:\n  name: {name}-{volume}\nspec:\n  accessModes: [\"ReadWriteOnce\"]\n  resources:\n    requests:\n      storage: {size}\n");
            documents.Add(DeploymentOf(name, labels, image, role));
        }

        var header = $"""
            # Made by the deploy plugin from `deploy {deployment.Name.Text}` in {source}, when the program was built: change that,
            # not this. Set the image to yours, then:  kubectl apply -f {Path.GetFileName(deployment.Target)}

            """;
        return header + string.Join("---\n", documents);
    }

    private static string Service(string name, string labels, string? type, List<(string Name, int Port, string Target)> ports)
    {
        var yaml = new StringBuilder();
        yaml.Append($"apiVersion: v1\nkind: Service\nmetadata:\n  name: {name}\n  labels:\n{Indent(labels, 4)}\nspec:\n");
        if (type is not null)
            yaml.Append($"  type: {type}\n");
        yaml.Append($"  selector:\n{Indent(labels, 4)}\n  ports:\n");
        foreach (var (portName, port, target) in ports)
            yaml.Append($"    - name: {portName}\n      port: {port}\n      targetPort: {target}\n");
        return yaml.ToString();
    }

    private static string DeploymentOf(string name, string labels, string image, Role role)
    {
        var yaml = new StringBuilder();
        yaml.Append($"apiVersion: apps/v1\nkind: Deployment\nmetadata:\n  name: {name}\nspec:\n  replicas: {role.Replicas}\n");
        if (role.Recreate)
            yaml.Append("  strategy:\n    type: Recreate\n");
        yaml.Append($"  selector:\n    matchLabels:\n{Indent(labels, 6)}\n  template:\n    metadata:\n      labels:\n{Indent(labels, 8)}\n    spec:\n");
        if (role.Stopping is { } stopping)
            yaml.Append($"      terminationGracePeriodSeconds: {stopping}\n");
        yaml.Append($"      containers:\n        - name: {Kebab(role.Name.Text)}\n          image: {Quote(image)}\n");
        if (role.Args.Count > 0)
        {
            yaml.Append("          args:\n");
            foreach (var argument in role.Args)
                yaml.Append($"            - {Quote(argument.Field is { } field ? $"$({Downward(field).Variable})" : argument.Text!)}\n");
        }

        // The downward API's values the arguments and settings use, first, so the others can refer to them.
        var fields = role.Args.Concat(role.Env.Select(e => e.Value)).Select(v => v.Field).OfType<string>().Distinct().ToList();
        if (fields.Count > 0 || role.Env.Count > 0)
        {
            yaml.Append("          env:\n");
            foreach (var field in fields)
            {
                var (variable, path) = Downward(field);
                yaml.Append($"            - name: {variable}\n              valueFrom:\n                fieldRef:\n                  fieldPath: {path}\n");
            }
            foreach (var (variable, value) in role.Env)
            {
                yaml.Append($"            - name: {variable}\n");
                if (value.Secret is { } secret)
                    yaml.Append($"              valueFrom:\n                secretKeyRef:\n                  name: {secret}\n                  key: {value.Key}\n");
                else if (value.Field is { } field)
                    yaml.Append($"              value: {Quote($"$({Downward(field).Variable})")}\n");
                else
                    yaml.Append($"              value: {Quote(value.Text!)}\n");
            }
        }
        if (role.Ports.Count > 0)
        {
            yaml.Append("          ports:\n");
            foreach (var port in role.Ports)
                yaml.Append($"            - name: {port.Name}\n              containerPort: {port.Number}\n");
        }
        var probePort = role.Ports.FirstOrDefault(p => p.Name == "http")?.Name ?? role.Ports.FirstOrDefault()?.Name;
        if (role.Ready is { } ready && probePort is not null)
            yaml.Append($"          readinessProbe:\n            httpGet:\n              path: {ready}\n              port: {probePort}\n            periodSeconds: 5\n");
        if (role.Health is { } health && probePort is not null)
            yaml.Append($"          livenessProbe:\n            httpGet:\n              path: {health}\n              port: {probePort}\n            periodSeconds: 10\n");
        if (role.Resources is var (cpu, memory, cpuLimit, memoryLimit))
        {
            yaml.Append("          resources:\n");
            if (cpu is not null || memory is not null)
            {
                yaml.Append("            requests:\n");
                if (cpu is not null) yaml.Append($"              cpu: {Quote(cpu)}\n");
                if (memory is not null) yaml.Append($"              memory: {Quote(memory)}\n");
            }
            if (cpuLimit is not null || memoryLimit is not null)
            {
                yaml.Append("            limits:\n");
                if (cpuLimit is not null) yaml.Append($"              cpu: {Quote(cpuLimit)}\n");
                if (memoryLimit is not null) yaml.Append($"              memory: {Quote(memoryLimit)}\n");
            }
        }
        if (role.Volumes.Count > 0)
        {
            yaml.Append("          volumeMounts:\n");
            foreach (var (volume, mount, _) in role.Volumes)
                yaml.Append($"            - name: {volume}\n              mountPath: {Quote(mount)}\n");
            yaml.Append("      volumes:\n");
            foreach (var (volume, _, _) in role.Volumes)
                yaml.Append($"        - name: {volume}\n          persistentVolumeClaim:\n            claimName: {name}-{volume}\n");
        }
        return yaml.ToString();
    }

    /// <summary>A PostgreSQL: its Secret (to change), Service, and StatefulSet with a volume for its data.</summary>
    private static IEnumerable<string> Postgres(string server, string database)
    {
        yield return $"""
            apiVersion: v1
            kind: Secret
            metadata:
              name: {server}
            type: Opaque
            stringData:
              password: change-me
              connection: {Quote($"Host={server};Database={database};Username={database};Password=change-me")}

            """;
        yield return $"""
            apiVersion: v1
            kind: Service
            metadata:
              name: {server}
            spec:
              selector:
                app: {server}
              ports:
                - port: 5432

            """;
        yield return $"""
            apiVersion: apps/v1
            kind: StatefulSet
            metadata:
              name: {server}
            spec:
              serviceName: {server}
              replicas: 1
              selector:
                matchLabels:
                  app: {server}
              template:
                metadata:
                  labels:
                    app: {server}
                spec:
                  containers:
                    - name: postgres
                      image: postgres:17
                      env:
                        - name: POSTGRES_DB
                          value: {database}
                        - name: POSTGRES_USER
                          value: {database}
                        - name: POSTGRES_PASSWORD
                          valueFrom:
                            secretKeyRef:
                              name: {server}
                              key: password
                        - name: PGDATA
                          value: /var/lib/postgresql/data/pgdata
                      ports:
                        - containerPort: 5432
                      volumeMounts:
                        - name: data
                          mountPath: /var/lib/postgresql/data
              volumeClaimTemplates:
                - metadata:
                    name: data
                  spec:
                    accessModes: ["ReadWriteOnce"]
                    resources:
                      requests:
                        storage: 1Gi

            """;
    }

    private static (string Variable, string Path) Downward(string field) =>
        field == "ip" ? ("POD_IP", "status.podIP") : ("POD_NAME", "metadata.name");

    private static string Indent(string text, int spaces) =>
        string.Join("\n", text.Split('\n').Select(line => new string(' ', spaces) + line));

    /// <summary><c>StockApi</c> as a Kubernetes name: <c>stock-api</c>.</summary>
    private static string Kebab(string name) => Regex.Replace(name, "(?<=[a-z0-9])([A-Z])", "-$1").ToLowerInvariant();

    /// <summary>A YAML string, quoted unless it's plainly a word (so "8000", "true" and "1" stay strings).</summary>
    private static string Quote(string text) =>
        Regex.IsMatch(text, "^[A-Za-z_./$()-][A-Za-z0-9_./:$()@-]*$") && text is not ("true" or "false" or "yes" or "no" or "null")
            ? text
            : "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
