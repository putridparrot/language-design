using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Piper.Plugins;
using Piper.Text;

namespace Piper.Tests;

/// <summary>
/// The deploy plugin (plugins/Piper.Plugins.Deploy): a `deploy` declaration, from which a build writes the Kubernetes
/// manifest beside it (the editor, checking, never does), and what's reported when it's written wrong. apps/stock,
/// apps/grid and apps/orders have theirs made so.
/// </summary>
public class DeployPluginTests
{
    private static readonly Lazy<ImmutableArray<IPiperPlugin>> Plugins = new(() =>
    {
        var (plugins, errors) = PluginLoader.Load(PluginsDirectory(), ["Piper.Plugins.Deploy/bin/plugin/Piper.Plugins.Deploy.dll"]);
        Assert.Empty(errors);
        return plugins;
    });

    private static string PluginsDirectory([CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "..", "plugins"));

    private static CompilationResult CompileIn(string folder, string text, WireShapeMode mode)
    {
        var path = Path.Combine(folder, "deploy.piper");
        File.WriteAllText(path, text);
        return Compilation.Compile([new SourceText(text, path)], new CompilationOptions { Plugins = Plugins.Value, WireShapes = mode });
    }

    private const string Shop = """
        fn Main() {}

        deploy Shop to "k8s/shop.yaml" {
            image "example.azurecr.io/shop:3";
            postgres "shop-db" database "shop";
            role api {
                replicas 2;
                args "--host", pod.ip, "--port", 8000;
                port http 8080 public 80;
                port actors 8000 service;
                health "/healthz" ready "/ready";
                env Shop__Connection = secret "shop-db" "connection";
                env Shop__Name = pod.name;
                stopping 1 minute;
                resources cpu "250m" memory "128Mi" limit memory "256Mi";
            }
            role ledger {
                recreate;
                volume data "/data" size "2Gi";
            }
        }
        """;

    [Fact]
    public void A_build_writes_the_manifest_and_the_editor_doesnt()
    {
        var folder = Directory.CreateTempSubdirectory("piper-deploy").FullName;
        try
        {
            var manifest = Path.Combine(folder, "k8s", "shop.yaml");
            Assert.True(CompileIn(folder, Shop, WireShapeMode.Check).Success);
            Assert.False(File.Exists(manifest));

            Assert.True(CompileIn(folder, Shop, WireShapeMode.Record).Success);
            var yaml = File.ReadAllText(manifest).ReplaceLineEndings("\n");
            Assert.StartsWith("# Made by the deploy plugin from `deploy Shop` in deploy.piper", yaml);
            string[] expected =
            [
                // The database: its Secret, Service and StatefulSet.
                "kind: Secret\nmetadata:\n  name: shop-db\n",
                "connection: \"Host=shop-db;Database=shop;Username=shop;Password=change-me\"",
                "kind: StatefulSet\nmetadata:\n  name: shop-db\n",
                // The public port behind a LoadBalancer named as the app, the service port behind one named as the role.
                "kind: Service\nmetadata:\n  name: shop\n",
                "  type: LoadBalancer\n",
                "    - name: http\n      port: 80\n      targetPort: http\n",
                "kind: Service\nmetadata:\n  name: shop-api\n",
                "    - name: actors\n      port: 8000\n      targetPort: actors\n",
                // The role, its arguments (the pod's IP from the downward API), settings and probes.
                "kind: Deployment\nmetadata:\n  name: shop-api\nspec:\n  replicas: 2\n",
                "      terminationGracePeriodSeconds: 60\n",
                "          image: example.azurecr.io/shop:3\n",
                "            - --host\n            - $(POD_IP)\n            - --port\n            - \"8000\"\n",
                "            - name: POD_IP\n              valueFrom:\n                fieldRef:\n                  fieldPath: status.podIP\n",
                "            - name: POD_NAME\n              valueFrom:\n                fieldRef:\n                  fieldPath: metadata.name\n",
                "            - name: Shop__Connection\n              valueFrom:\n                secretKeyRef:\n                  name: shop-db\n                  key: connection\n",
                "            - name: Shop__Name\n              value: $(POD_NAME)\n",
                "          readinessProbe:\n            httpGet:\n              path: /ready\n              port: http\n",
                "          livenessProbe:\n            httpGet:\n              path: /healthz\n              port: http\n",
                "            requests:\n              cpu: \"250m\"\n              memory: \"128Mi\"\n            limits:\n              memory: \"256Mi\"\n",
                // The other role: one pod at a time, with its volume.
                "kind: PersistentVolumeClaim\nmetadata:\n  name: shop-ledger-data\n",
                "      storage: 2Gi\n",
                "  name: shop-ledger\nspec:\n  replicas: 1\n  strategy:\n    type: Recreate\n",
                "          volumeMounts:\n            - name: data\n              mountPath: /data\n      volumes:\n        - name: data\n          persistentVolumeClaim:\n            claimName: shop-ledger-data\n",
            ];
            foreach (var part in expected)
                Assert.True(yaml.Contains(part), $"Missing:\n{part}\nfrom:\n{yaml}");

            // Built again unchanged: the file's left alone.
            var written = File.GetLastWriteTimeUtc(manifest);
            Assert.True(CompileIn(folder, Shop, WireShapeMode.Record).Success);
            Assert.Equal(written, File.GetLastWriteTimeUtc(manifest));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Theory]
    [InlineData("deploy D { role a { replica 2; } }", "replica", "'replica' isn't part of a role")]
    [InlineData("deploy D { service a; }", "service", "Expected 'role', 'postgres' or 'image'")]
    [InlineData("deploy D { role a { port http 80 public; } role b { port http 81 public; } }", "D", "Only one role can have public ports")]
    [InlineData("deploy D { role a { replicas 2; volume data \"/data\" size \"1Gi\"; } }", "a {", "a has a volume, which one pod at a time can mount, but 2 replicas")]
    [InlineData("deploy D { role a { args 1 + 2; } }", "1 + 2", "Expected a string, a number, `pod.ip`, `pod.name`")]
    public void Mistakes_in_a_deployment_are_reported(string deployment, string at, string message)
    {
        var text = "fn Main() {}\n" + deployment;
        var result = Compilation.Compile([new SourceText(text, "main.piper")], new CompilationOptions { Plugins = Plugins.Value });
        var error = Assert.Single(result.Diagnostics, d => d.Message.Contains(message));
        Assert.Equal(text.IndexOf(at, text.IndexOf("deploy", StringComparison.Ordinal), StringComparison.Ordinal), error.Span.Start);
    }
}
