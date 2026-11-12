using System.Diagnostics;
using System.Text.Json;

namespace TaskbarQuota.Tests;

public class BuildScriptTests
{
    [Theory]
    [InlineData("1.0.5", "1.0.5.0", 5)]
    [InlineData("2026.11.11-manual", "2026.11.11.0", 11)]
    [InlineData("1.2.3-rc.1+build.42", "1.2.3.0", 3)]
    public async Task BuildVersionSeparatesNumericCoreFromPrerelease(string version, string assembly, int application)
    {
        var script = Path.Combine(RepositoryRoot(), "tools", "Get-BuildVersion.ps1");
        var result = await RunScript("param($Script, $Version)\n& $Script -Version $Version | ConvertTo-Json -Compress", script, version);
        Assert.True(result.ExitCode == 0, result.Error);
        using var json = JsonDocument.Parse(result.Output);
        Assert.Equal(assembly, json.RootElement.GetProperty("AssemblyVersion").GetString());
        Assert.Equal(application, json.RootElement.GetProperty("ApplicationVersion").GetInt32());
    }

    [Theory]
    [InlineData("1.2.3';throw 'injected")]
    [InlineData("99999999999999999.2.3")]
    [InlineData("1.2")]
    [InlineData("1.2.65535")]
    public async Task InvalidBuildVersionIsRejected(string version)
    {
        var result = await RunPowerShell("-File", Path.Combine(RepositoryRoot(), "tools", "Get-BuildVersion.ps1"), "-Version", version);
        Assert.NotEqual(0, result.ExitCode);
    }

    [Theory]
    [InlineData("artifacts")]
    [InlineData("local")]
    [InlineData("local.previous")]
    public async Task LocalBuildRejectsJunctionBeforeWritingOrPublishing(string linkName)
    {
        var fixture = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "TaskbarQuota-build-" + Guid.NewGuid().ToString("N")));
        var repository = Path.Combine(fixture, "repository");
        var external = Path.Combine(fixture, "external");
        var artifacts = Path.Combine(repository, "artifacts");
        string link = linkName == "artifacts" ? artifacts : Path.Combine(artifacts, linkName);
        Directory.CreateDirectory(Path.Combine(repository, "tools"));
        Directory.CreateDirectory(external);
        File.WriteAllText(Path.Combine(external, "TaskbarQuota.exe"), "sentinel");
        var copiedScript = Path.Combine(repository, "tools", "Build-Local.ps1");
        File.Copy(Path.Combine(RepositoryRoot(), "tools", "Build-Local.ps1"), copiedScript);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(link)!);
            // Junction creation doesn't require symbolic-link privileges on Windows.
            var create = await RunScript("param($Link, $Target)\n$ErrorActionPreference = 'Stop'\nNew-Item -ItemType Junction -Path $Link -Target $Target | Out-Null", link, external);
            Assert.True(create.ExitCode == 0, create.Error);
            var build = await RunPowerShell("-File", copiedScript, "-NoStart");
            Assert.NotEqual(0, build.ExitCode);
            Assert.Contains("Expected ordinary build path ancestors", build.Error);
            Assert.DoesNotContain("Building TaskbarQuota", build.Output);
            Assert.Equal("sentinel", File.ReadAllText(Path.Combine(external, "TaskbarQuota.exe")));
            Assert.Single(Directory.GetFileSystemEntries(external));
            Assert.False(File.Exists(Path.Combine(artifacts, "local-build.lock")));
        }
        finally
        {
            // Unlink the junction before deleting only this explicitly created fixture.
            if (Directory.Exists(link) && (File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0)
                Directory.Delete(link);
            Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), fixture, StringComparison.OrdinalIgnoreCase);
            Directory.Delete(fixture, recursive: true);
        }
    }

    [Theory]
    [InlineData("local", true, 1)]
    [InlineData("exited", true, 0)]
    [InlineData("foreign", false, 0)]
    public async Task LocalBuildRetainsValidatedProcessIdentity(string mode, bool succeeds, int kills)
    {
        var fixture = Directory.CreateTempSubdirectory("TaskbarQuota-build-process-").FullName;
        try
        {
            var tools = Path.Combine(fixture, "tools");
            Directory.CreateDirectory(tools);
            var script = Path.Combine(tools, "Build-Local.ps1");
            File.Copy(Path.Combine(RepositoryRoot(), "tools", "Build-Local.ps1"), script);
            var result = await RunScript("""
                param($Script, $Mode)
                $ErrorActionPreference = 'Stop'
                $root = Split-Path (Split-Path $Script)
                $local = Join-Path $root 'artifacts/local'
                $previous = Join-Path $root 'artifacts/local.previous'
                New-Item -ItemType Directory -Path $local,$previous -Force | Out-Null
                Set-Content (Join-Path $local 'TaskbarQuota.exe') 'old'
                Set-Content (Join-Path $previous 'TaskbarQuota.exe') 'backup'
                $global:fixtureProcess = [pscustomobject]@{
                    Pinned=$false; Exited=$false; Disposed=$false; Kills=0; Mode=$Mode
                    Executable=if($Mode -eq 'foreign'){'C:/another-copy/TaskbarQuota.exe'}else{Join-Path $local 'TaskbarQuota.exe'}
                }
                $fixtureProcess | Add-Member ScriptProperty Handle {
                    $this.Pinned=$true
                    if($this.Mode -eq 'exited'){$this.Exited=$true}
                    return 42
                }
                $fixtureProcess | Add-Member ScriptProperty HasExited {$this.Exited}
                $fixtureProcess | Add-Member ScriptProperty Path {
                    if(!$this.Pinned){throw 'Executable was inspected before retaining the process handle'}
                    $this.Executable
                }
                $fixtureProcess | Add-Member ScriptProperty Id {throw 'PID must not be reopened after validation'}
                $fixtureProcess | Add-Member ScriptMethod Kill {
                    if(!$this.Pinned){throw 'Process identity was not retained'}
                    $this.Kills++; $this.Exited=$true
                }
                $fixtureProcess | Add-Member ScriptMethod WaitForExit {param($Timeout) $this.Exited}
                $fixtureProcess | Add-Member ScriptMethod Dispose {$this.Disposed=$true}
                function global:Get-Process {$global:fixtureProcess}
                function global:Stop-Process {throw 'PID-based termination must not be used'}
                function global:dotnet {
                    $destination=$args[[Array]::IndexOf($args,'-o')+1]
                    New-Item -ItemType Directory -Path $destination -Force | Out-Null
                    Set-Content (Join-Path $destination 'TaskbarQuota.exe') 'new'
                    $global:LASTEXITCODE=0
                }
                $succeeded=$false; $failure=''
                try {& $Script -NoStart; $succeeded=$true} catch {$failure=$_.Exception.Message}
                'fixture-result:'+(@{
                    succeeded=$succeeded; failure=$failure; pinned=$fixtureProcess.Pinned
                    disposed=$fixtureProcess.Disposed; kills=$fixtureProcess.Kills
                    backup=if(Test-Path (Join-Path $previous 'TaskbarQuota.exe')){(Get-Content (Join-Path $previous 'TaskbarQuota.exe')).Trim()}else{''}
                } | ConvertTo-Json -Compress)
                """, script, mode);
            Assert.True(result.ExitCode == 0, result.Error);
            var output = Assert.Single(result.Output.Split('\n'), line => line.StartsWith("fixture-result:", StringComparison.Ordinal));
            using var json = JsonDocument.Parse(output["fixture-result:".Length..]);
            Assert.Equal(succeeds, json.RootElement.GetProperty("succeeded").GetBoolean());
            Assert.Equal(kills, json.RootElement.GetProperty("kills").GetInt32());
            Assert.True(json.RootElement.GetProperty("pinned").GetBoolean());
            Assert.True(json.RootElement.GetProperty("disposed").GetBoolean());
            if (!succeeds)
            {
                Assert.Contains("A different TaskbarQuota copy is running", json.RootElement.GetProperty("failure").GetString());
                Assert.Equal("backup", json.RootElement.GetProperty("backup").GetString());
            }
            else
                Assert.Equal("old", json.RootElement.GetProperty("backup").GetString());
        }
        finally
        {
            Assert.Equal(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), Path.GetDirectoryName(fixture));
            Directory.Delete(fixture, recursive: true);
        }
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "tools", "Build-Local.ps1")))
                return directory.FullName;
        throw new DirectoryNotFoundException("Repository build scripts weren't found.");
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunPowerShell(params string[] arguments)
    {
        var start = new ProcessStartInfo("pwsh")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw new TimeoutException("Build script fixture exceeded 30 seconds.");
        }
        return (process.ExitCode, await output, await error);
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunScript(string content, params string[] arguments)
    {
        var script = Path.Combine(Path.GetTempPath(), "TaskbarQuota-script-" + Guid.NewGuid().ToString("N") + ".ps1");
        File.WriteAllText(script, content);
        try { return await RunPowerShell(["-File", script, .. arguments]); }
        finally { File.Delete(script); }
    }
}
