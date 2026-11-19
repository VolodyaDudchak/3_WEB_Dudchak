using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;

public static class Db
{
    public static string ConnStr => Environment.GetEnvironmentVariable("CONN_STR")!;

    public static async Task InitAsync()
    {
        for (int i = 0; i < 10; i++)
        {
            try
            {
                await using var conn = new NpgsqlConnection(ConnStr);
                await conn.OpenAsync();
                var sql = """
                    CREATE TABLE IF NOT EXISTS users (
                        id SERIAL PRIMARY KEY,
                        username TEXT UNIQUE NOT NULL,
                        password_hash TEXT NOT NULL,
                        role TEXT NOT NULL
                    );
                    CREATE TABLE IF NOT EXISTS tasks (
                        id SERIAL PRIMARY KEY,
                        user_id INT NOT NULL REFERENCES users(id),
                        n INT NOT NULL,
                        seed INT NOT NULL,
                        status TEXT NOT NULL,
                        progress INT NOT NULL DEFAULT 0,
                        result_json TEXT,
                        error TEXT,
                        worker_id TEXT,
                        created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                        started_at TIMESTAMPTZ,
                        finished_at TIMESTAMPTZ
                    );
                    CREATE TABLE IF NOT EXISTS workers (
                        worker_id TEXT PRIMARY KEY,
                        node_name TEXT NOT NULL,
                        cpu_percent DOUBLE PRECISION NOT NULL,
                        ram_mb DOUBLE PRECISION NOT NULL,
                        active_tasks INT NOT NULL,
                        last_heartbeat TIMESTAMPTZ NOT NULL DEFAULT NOW()
                    );
                    """;
                await using var cmd = new NpgsqlCommand(sql, conn);
                await cmd.ExecuteNonQueryAsync();

                var adminHash = Hash("admin");
                await using var seedCmd = new NpgsqlCommand(
                    "INSERT INTO users(username, password_hash, role) VALUES ('admin', @h, 'Admin') ON CONFLICT DO NOTHING", conn);
                seedCmd.Parameters.AddWithValue("h", adminHash);
                await seedCmd.ExecuteNonQueryAsync();
                return;
            }
            catch
            {
                await Task.Delay(2000);
            }
        }
    }

    public static string Hash(string input) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)));
}

public static class SlaeSolver
{
    public static async Task<string> SolveAsync(
        int n,
        int seed,
        Func<int, Task<bool>> onProgressAndCheckCancel,
        CancellationToken ct)
    {
        var rng = new Random(seed);
        var a = new double[n][];
        var b = new double[n];
        var aOrig = new double[n][];
        var bOrig = new double[n];

        for (int i = 0; i < n; i++)
        {
            a[i] = new double[n];
            aOrig[i] = new double[n];
            double rowSum = 0;
            for (int j = 0; j < n; j++)
            {
                double val = rng.NextDouble() * 2 - 1;
                a[i][j] = val;
                aOrig[i][j] = val;
                rowSum += Math.Abs(val);
            }
            a[i][i] = rowSum + 1.0;
            aOrig[i][i] = a[i][i];
            b[i] = rng.NextDouble() * 10;
            bOrig[i] = b[i];
        }

        var sw = Stopwatch.StartNew();
        int lastReport = -1;

        for (int k = 0; k < n; k++)
        {
            ct.ThrowIfCancellationRequested();

            int pivot = k;
            double maxVal = Math.Abs(a[k][k]);
            for (int i = k + 1; i < n; i++)
            {
                double v = Math.Abs(a[i][k]);
                if (v > maxVal) { maxVal = v; pivot = i; }
            }

            if (pivot != k)
            {
                (a[k], a[pivot]) = (a[pivot], a[k]);
                (b[k], b[pivot]) = (b[pivot], b[k]);
            }

            double diag = a[k][k];
            for (int i = k + 1; i < n; i++)
            {
                double factor = a[i][k] / diag;
                var rowI = a[i];
                var rowK = a[k];
                for (int j = k + 1; j < n; j++)
                    rowI[j] -= factor * rowK[j];
                b[i] -= factor * b[k];
            }

            int pct = (int)((1.0 - Math.Pow(1.0 - (double)(k + 1) / n, 3)) * 95);
            if (pct > lastReport && sw.ElapsedMilliseconds >= 400)
            {
                lastReport = pct;
                sw.Restart();
                if (await onProgressAndCheckCancel(pct))
                    throw new OperationCanceledException();
            }
        }

        var x = new double[n];
        for (int i = n - 1; i >= 0; i--)
        {
            double sum = b[i];
            for (int j = i + 1; j < n; j++)
                sum -= a[i][j] * x[j];
            x[i] = sum / a[i][i];
        }

        double maxResidual = 0;
        for (int i = 0; i < n; i++)
        {
            double s = 0;
            for (int j = 0; j < n; j++) s += aOrig[i][j] * x[j];
            maxResidual = Math.Max(maxResidual, Math.Abs(s - bOrig[i]));
        }

        return JsonSerializer.Serialize(new
        {
            n,
            residual = maxResidual,
            first5 = x.Take(5).Select(v => Math.Round(v, 6)).ToArray()
        });
    }
}