// See https://aka.ms/new-console-template for more information
using System.Diagnostics;
using Spectre.Console;

// ── Prompts ────────────────────────────────────────────────────────────────
string rootFolder = AnsiConsole.Ask<string>(
    "[yellow]Root folder[/] containing cloned repos:");

while (!Directory.Exists(rootFolder))
{
    AnsiConsole.MarkupLine("[red]Folder not found.[/] Try again.");
    rootFolder = AnsiConsole.Ask<string>("[yellow]Root folder[/]:");
}

int yearA = AnsiConsole.Ask<int>("Compare [green]Year A[/] (older):", 2024);
int yearB = AnsiConsole.Ask<int>("    vs. [green]Year B[/] (newer):", 2025);

int startMonth = AnsiConsole.Ask<int>("Start month [grey](1-12)[/]:", 1);
int endMonth   = AnsiConsole.Ask<int>("End month   [grey](1-12)[/]:", 3);

string sinceA = $"{yearA}-{startMonth:D2}-01";
string untilA = $"{yearA}-{endMonth:D2}-{DateTime.DaysInMonth(yearA, endMonth):D2}";
string sinceB = $"{yearB}-{startMonth:D2}-01";
string untilB = $"{yearB}-{endMonth:D2}-{DateTime.DaysInMonth(yearB, endMonth):D2}";

// ── Discover repos ─────────────────────────────────────────────────────────
var repoDirs = Directory.GetDirectories(rootFolder, ".git", SearchOption.AllDirectories)
    .Select(g => Directory.GetParent(g)!.FullName)
    .OrderBy(p => p)
    .ToList();

if (repoDirs.Count == 0)
{
    AnsiConsole.MarkupLine("[red]No git repositories found under that folder.[/]");
    return;
}

AnsiConsole.MarkupLine($"\nFound [cyan]{repoDirs.Count}[/] repositories. Gathering stats...\n");

// ── Gather stats ───────────────────────────────────────────────────────────
var results = new System.Collections.Concurrent.ConcurrentBag<RepoStats>();

await AnsiConsole.Progress()
    .AutoRefresh(true)
    .Columns(
        new TaskDescriptionColumn(),
        new ProgressBarColumn(),
        new PercentageColumn(),
        new SpinnerColumn())
    .StartAsync(async ctx =>
    {
        var task = ctx.AddTask("[green]Processing repos[/]", maxValue: repoDirs.Count);

        await Parallel.ForEachAsync(
            repoDirs,
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
            async (repoPath, ct) =>
            {
                string name = Path.GetFileName(repoPath);
                var (addA, delA) = await GetStats(repoPath, sinceA, untilA);
                var (addB, delB) = await GetStats(repoPath, sinceB, untilB);

                results.Add(new RepoStats(name, addA, delA, addB, delB));
                task.Increment(1);
            });
    });

// ── Render table ───────────────────────────────────────────────────────────
var sorted = results.OrderBy(r => r.Name).ToList();

var table = new Table()
    .Border(TableBorder.Rounded)
    .AddColumn(new TableColumn("[white]Repository[/]"))
    .AddColumn(new TableColumn($"[green]{yearA} Added[/]").RightAligned())
    .AddColumn(new TableColumn($"[red]{yearA} Deleted[/]").RightAligned())
    .AddColumn(new TableColumn($"[green]{yearB} Added[/]").RightAligned())
    .AddColumn(new TableColumn($"[red]{yearB} Deleted[/]").RightAligned())
    .AddColumn(new TableColumn("[cyan]Δ Added[/]").RightAligned())
    .AddColumn(new TableColumn("[cyan]Δ Deleted[/]").RightAligned());

long totalAddA = 0, totalDelA = 0, totalAddB = 0, totalDelB = 0;

foreach (var r in sorted)
{
    totalAddA += r.AddedA; totalDelA += r.DeletedA;
    totalAddB += r.AddedB; totalDelB += r.DeletedB;

    long deltaAdd = r.AddedB  - r.AddedA;
    long deltaDel = r.DeletedB - r.DeletedA;

    table.AddRow(
        Markup.Escape(r.Name),
        $"[green]{r.AddedA:N0}[/]",
        $"[red]{r.DeletedA:N0}[/]",
        $"[green]{r.AddedB:N0}[/]",
        $"[red]{r.DeletedB:N0}[/]",
        DeltaMarkup(deltaAdd),
        DeltaMarkup(deltaDel));
}

// Totals row
table.AddEmptyRow();
table.AddRow(
    "[bold]TOTAL[/]",
    $"[bold green]{totalAddA:N0}[/]",
    $"[bold red]{totalDelA:N0}[/]",
    $"[bold green]{totalAddB:N0}[/]",
    $"[bold red]{totalDelB:N0}[/]",
    $"[bold]{DeltaMarkup(totalAddB - totalAddA)}[/]",
    $"[bold]{DeltaMarkup(totalDelB - totalDelA)}[/]");

AnsiConsole.Write(table);
AnsiConsole.MarkupLine($"\n[grey]Date range: {startMonth:D2}/01 – end of month {endMonth:D2}[/]");

// ── Helpers ────────────────────────────────────────────────────────────────
static async Task<(long added, long deleted)> GetStats(string repoPath, string since, string until)
{
    var psi = new ProcessStartInfo("git")
    {
        Arguments = $"log --since={since} --until={until} --pretty=tformat: --numstat",
        WorkingDirectory = repoPath,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true
    };

    using var process = Process.Start(psi)!;
    string output = await process.StandardOutput.ReadToEndAsync();
    await process.WaitForExitAsync();

    long added = 0, deleted = 0;

    foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
    {
        // numstat format: <added>\t<deleted>\t<filename>
        // Binary files show "-" instead of numbers
        var parts = line.Split('\t');
        if (parts.Length >= 2
            && long.TryParse(parts[0], out long a)
            && long.TryParse(parts[1], out long d))
        {
            added   += a;
            deleted += d;
        }
    }

    return (added, deleted);
}

static string DeltaMarkup(long delta) =>
    delta switch
    {
        > 0 => $"[green]+{delta:N0}[/]",
        < 0 => $"[red]{delta:N0}[/]",
        _   => "[grey]0[/]"
    };

// ── Record ─────────────────────────────────────────────────────────────────
record RepoStats(string Name, long AddedA, long DeletedA, long AddedB, long DeletedB);
