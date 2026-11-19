using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Npgsql;

await Db.InitAsync();

var roleMode = Environment.GetEnvironmentVariable("ROLE") ?? "WORKER";
if (roleMode == "AUTOSCALER")
{
    await RunAutoscalerAsync();
    return;
}

var builder = WebApplication.CreateBuilder(args);
var jwtSecret = Environment.GetEnvironmentVariable("JWT_SECRET")!;
var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret));

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o => o.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = false,
        ValidateAudience = false,
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = key
    });
builder.Services.AddAuthorization();
builder.Services.AddHostedService<WorkerService>();

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();

int maxN = int.Parse(Environment.GetEnvironmentVariable("MAX_N") ?? "2500");
int maxActivePerUser = int.Parse(Environment.GetEnvironmentVariable("MAX_ACTIVE_PER_USER") ?? "3");
int maxSlots = int.Parse(Environment.GetEnvironmentVariable("MAX_SLOTS") ?? "2");

app.MapPost("/api/auth/register", async (AuthReq req) =>
{
    await using var conn = new NpgsqlConnection(Db.ConnStr);
    await conn.OpenAsync();
    try
    {
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO users(username, password_hash, role) VALUES (@u, @p, 'User')", conn);
        cmd.Parameters.AddWithValue("u", req.Username);
        cmd.Parameters.AddWithValue("p", Db.Hash(req.Password));
        await cmd.ExecuteNonQueryAsync();
        return Results.Ok();
    }
    catch { return Results.BadRequest(new { error = "User already exists" }); }
});

app.MapPost("/api/auth/login", async (AuthReq req) =>
{
    await using var conn = new NpgsqlConnection(Db.ConnStr);
    await conn.OpenAsync();
    await using var cmd = new NpgsqlCommand(
        "SELECT id, role FROM users WHERE username=@u AND password_hash=@p", conn);
    cmd.Parameters.AddWithValue("u", req.Username);
    cmd.Parameters.AddWithValue("p", Db.Hash(req.Password));
    await using var r = await cmd.ExecuteReaderAsync();
    if (!await r.ReadAsync()) return Results.Unauthorized();

    var id = r.GetInt32(0);
    var role = r.GetString(1);
    var token = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
        claims: new[] { new Claim("uid", id.ToString()), new Claim(ClaimTypes.Role, role) },
        expires: DateTime.UtcNow.AddHours(12),
        signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256)));
    return Results.Ok(new { token, role, maxN });
});

app.MapPost("/api/tasks", async (TaskReq req, ClaimsPrincipal user) =>
{
    if (req.N < 2 || req.N > maxN)
        return Results.BadRequest(new { error = $"N must be between 2 and {maxN}" });

    int uid = int.Parse(user.FindFirst("uid")!.Value);
    await using var conn = new NpgsqlConnection(Db.ConnStr);
    await conn.OpenAsync();

    await using var cntCmd = new NpgsqlCommand(
        "SELECT COUNT(*) FROM tasks WHERE user_id=@u AND status IN ('Queued','Running')", conn);
    cntCmd.Parameters.AddWithValue("u", uid);
    if ((long)(await cntCmd.ExecuteScalarAsync())! >= maxActivePerUser)
        return Results.StatusCode(429);

    await using var ins = new NpgsqlCommand(
        "INSERT INTO tasks(user_id, n, seed, status) VALUES (@u, @n, @s, 'Queued') RETURNING id", conn);
    ins.Parameters.AddWithValue("u", uid);
    ins.Parameters.AddWithValue("n", req.N);
    ins.Parameters.AddWithValue("s", req.Seed);
    int id = (int)(await ins.ExecuteScalarAsync())!;
    return Results.Ok(new { id });
}).RequireAuthorization();

app.MapGet("/api/tasks", async (ClaimsPrincipal user) =>
{
    int uid = int.Parse(user.FindFirst("uid")!.Value);
    await using var conn = new NpgsqlConnection(Db.ConnStr);
    await conn.OpenAsync();

    await using var statCmd = new NpgsqlCommand("""
        SELECT
            COALESCE((SELECT AVG(EXTRACT(EPOCH FROM (finished_at - started_at))) FROM tasks WHERE status='Completed'), 10),
            GREATEST((SELECT COUNT(*) FROM workers WHERE last_heartbeat > NOW() - INTERVAL '10 seconds'), 1)
        """, conn);
    await using var sr = await statCmd.ExecuteReaderAsync();
    await sr.ReadAsync();
    double avgSec = sr.GetDouble(0);
    long aliveWorkers = sr.GetInt64(1);
    await sr.CloseAsync();

    await using var cmd = new NpgsqlCommand("""
        SELECT id, n, seed, status, progress, result_json, error, worker_id, created_at,
               (SELECT COUNT(*) FROM tasks t2 WHERE t2.status='Queued' AND t2.created_at < t1.created_at) AS qpos
        FROM tasks t1 WHERE user_id=@u ORDER BY created_at DESC LIMIT 30
        """, conn);
    cmd.Parameters.AddWithValue("u", uid);
    await using var r = await cmd.ExecuteReaderAsync();
    var list = new List<object>();
    while (await r.ReadAsync())
    {
        var status = r.GetString(3);
        long qpos = r.GetInt64(9) + 1;
        int? eta = status == "Queued" ? (int)Math.Ceiling(qpos * avgSec / (aliveWorkers * maxSlots)) : null;
        list.Add(new
        {
            id = r.GetInt32(0), n = r.GetInt32(1), seed = r.GetInt32(2),
            status, progress = r.GetInt32(4),
            result = r.IsDBNull(5) ? null : r.GetString(5),
            error = r.IsDBNull(6) ? null : r.GetString(6),
            worker = r.IsDBNull(7) ? null : r.GetString(7),
            queuePos = status == "Queued" ? qpos : (long?)null,
            etaSec = eta
        });
    }
    return Results.Ok(list);
}).RequireAuthorization();

app.MapPost("/api/tasks/{id:int}/cancel", async (int id, ClaimsPrincipal user) =>
{
    int uid = int.Parse(user.FindFirst("uid")!.Value);
    await using var conn = new NpgsqlConnection(Db.ConnStr);
    await conn.OpenAsync();
    await using var cmd = new NpgsqlCommand("""
        UPDATE tasks SET status = CASE WHEN status='Queued' THEN 'Cancelled' ELSE 'Cancelling' END
        WHERE id=@id AND user_id=@u AND status IN ('Queued','Running')
        """, conn);
    cmd.Parameters.AddWithValue("id", id);
    cmd.Parameters.AddWithValue("u", uid);
    await cmd.ExecuteNonQueryAsync();
    return Results.Ok();
}).RequireAuthorization();

app.MapGet("/api/admin/stats", async () =>
{
    await using var conn = new NpgsqlConnection(Db.ConnStr);
    await conn.OpenAsync();

    var workers = new List<object>();
    await using (var cmd = new NpgsqlCommand(
        "SELECT worker_id, node_name, cpu_percent, ram_mb, active_tasks FROM workers WHERE last_heartbeat > NOW() - INTERVAL '10 seconds' ORDER BY worker_id", conn))
    await using (var r = await cmd.ExecuteReaderAsync())
        while (await r.ReadAsync())
            workers.Add(new { id = r.GetString(0), node = r.GetString(1), cpu = Math.Round(r.GetDouble(2), 1), ram = Math.Round(r.GetDouble(3), 1), active = r.GetInt32(4) });

    var activeTasks = new List<object>();
    await using (var cmd = new NpgsqlCommand(
        "SELECT t.id, u.username, t.n, t.status, t.progress, t.worker_id FROM tasks t JOIN users u ON u.id=t.user_id WHERE t.status IN ('Queued','Running','Cancelling') ORDER BY t.created_at ASC", conn))
    await using (var r = await cmd.ExecuteReaderAsync())
        while (await r.ReadAsync())
            activeTasks.Add(new { id = r.GetInt32(0), user = r.GetString(1), n = r.GetInt32(2), status = r.GetString(3), progress = r.GetInt32(4), worker = r.IsDBNull(5) ? "-" : r.GetString(5) });

    return Results.Ok(new { workers, activeTasks });
}).RequireAuthorization(p => p.RequireRole("Admin"));

app.Run("http://0.0.0.0:8080");

static async Task RunAutoscalerAsync()
{
    var serviceName = Environment.GetEnvironmentVariable("SERVICE_NAME") ?? "slae_backend";
    ulong minRep = ulong.Parse(Environment.GetEnvironmentVariable("MIN_REPLICAS") ?? "2");
    ulong maxRep = ulong.Parse(Environment.GetEnvironmentVariable("MAX_REPLICAS") ?? "5");
    using var docker = new DockerClientConfiguration(new Uri("unix:///var/run/docker.sock")).CreateClient();
    DateTime lastScale = DateTime.MinValue;

    while (true)
    {
        try
        {
            await using var conn = new NpgsqlConnection(Db.ConnStr);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand("SELECT COUNT(*) FROM tasks WHERE status='Queued'", conn);
            long queued = (long)(await cmd.ExecuteScalarAsync())!;

            var svc = await docker.Swarm.InspectServiceAsync(serviceName);
            ulong current = svc.Spec.Mode.Replicated.Replicas ?? minRep;
            ulong target = current;

            if (queued >= 2 && current < maxRep && (DateTime.UtcNow - lastScale).TotalSeconds > 15)
                target = current + 1;
            else if (queued == 0 && current > minRep && (DateTime.UtcNow - lastScale).TotalSeconds > 60)
                target = current - 1;

            if (target != current)
            {
                svc.Spec.Mode.Replicated.Replicas = target;
                await docker.Swarm.UpdateServiceAsync(svc.ID, new ServiceUpdateParameters
                {
                    Service = svc.Spec,
                    Version = (long)svc.Version.Index
                });
                lastScale = DateTime.UtcNow;
                Console.WriteLine($"Scaled {serviceName}: {current} -> {target}");
            }
        }
        catch (Exception ex) { Console.WriteLine(ex.Message); }
        await Task.Delay(5000);
    }
}

record AuthReq(string Username, string Password);
record TaskReq(int N, int Seed);

class WorkerService : BackgroundService
{
    private readonly string _workerId = Environment.MachineName;
    private readonly string _nodeName = Environment.GetEnvironmentVariable("NODE_NAME") ?? "local";
    private readonly int _maxSlots = int.Parse(Environment.GetEnvironmentVariable("MAX_SLOTS") ?? "2");
    private readonly int _timeoutSec = int.Parse(Environment.GetEnvironmentVariable("TASK_TIMEOUT_SEC") ?? "60");
    private int _active;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _ = HeartbeatLoopAsync(stoppingToken);
        var sem = new SemaphoreSlim(_maxSlots);

        while (!stoppingToken.IsCancellationRequested)
        {
            await sem.WaitAsync(stoppingToken);
            var task = await ClaimTaskAsync();
            if (task == null)
            {
                sem.Release();
                await Task.Delay(500, stoppingToken);
                continue;
            }

            Interlocked.Increment(ref _active);
            _ = Task.Run(async () =>
            {
                try { await ProcessTaskAsync(task.Value.Id, task.Value.N, task.Value.Seed); }
                finally { Interlocked.Decrement(ref _active); sem.Release(); }
            }, stoppingToken);
        }
    }

    private async Task<(int Id, int N, int Seed)?> ClaimTaskAsync()
    {
        await using var conn = new NpgsqlConnection(Db.ConnStr);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await using var cmd = new NpgsqlCommand("""
            SELECT id, n, seed FROM tasks WHERE status='Queued'
            ORDER BY created_at ASC FOR UPDATE SKIP LOCKED LIMIT 1
            """, conn, tx);
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) return null;
        var res = (r.GetInt32(0), r.GetInt32(1), r.GetInt32(2));
        await r.CloseAsync();

        await using var upd = new NpgsqlCommand(
            "UPDATE tasks SET status='Running', worker_id=@w, started_at=NOW() WHERE id=@id", conn, tx);
        upd.Parameters.AddWithValue("w", _workerId);
        upd.Parameters.AddWithValue("id", res.Item1);
        await upd.ExecuteNonQueryAsync();
        await tx.CommitAsync();
        return res;
    }

    private async Task ProcessTaskAsync(int id, int n, int seed)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(_timeoutSec));
        try
        {
            var result = await SlaeSolver.SolveAsync(n, seed, async pct =>
            {
                await using var conn = new NpgsqlConnection(Db.ConnStr);
                await conn.OpenAsync();
                await using var cmd = new NpgsqlCommand(
                    "UPDATE tasks SET progress=@p WHERE id=@id RETURNING status", conn);
                cmd.Parameters.AddWithValue("p", pct);
                cmd.Parameters.AddWithValue("id", id);
                var st = (string)(await cmd.ExecuteScalarAsync())!;
                return st == "Cancelling";
            }, cts.Token);

            await SetFinalAsync(id, "Completed", 100, result, null);
        }
        catch (OperationCanceledException)
        {
            var status = cts.IsCancellationRequested ? "Failed" : "Cancelled";
            var err = cts.IsCancellationRequested ? "Timeout exceeded" : null;
            await SetFinalAsync(id, status, 0, null, err);
        }
        catch (Exception ex)
        {
            await SetFinalAsync(id, "Failed", 0, null, ex.Message);
        }
    }

    private static async Task SetFinalAsync(int id, string status, int progress, string? res, string? err)
    {
        await using var conn = new NpgsqlConnection(Db.ConnStr);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "UPDATE tasks SET status=@s, progress=@p, result_json=@r, error=@e, finished_at=NOW() WHERE id=@id", conn);
        cmd.Parameters.AddWithValue("s", status);
        cmd.Parameters.AddWithValue("p", progress);
        cmd.Parameters.AddWithValue("r", (object?)res ?? DBNull.Value);
        cmd.Parameters.AddWithValue("e", (object?)err ?? DBNull.Value);
        cmd.Parameters.AddWithValue("id", id);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        var proc = Process.GetCurrentProcess();
        var prevCpu = proc.TotalProcessorTime;
        var prevTime = DateTime.UtcNow;

        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(3000, ct);
            var nowCpu = proc.TotalProcessorTime;
            var nowTime = DateTime.UtcNow;
            double cpuPct = (nowCpu - prevCpu).TotalMilliseconds /
                            ((nowTime - prevTime).TotalMilliseconds * Environment.ProcessorCount) * 100;
            prevCpu = nowCpu; prevTime = nowTime;
            double ramMb = proc.WorkingSet64 / 1048576.0;

            try
            {
                await using var conn = new NpgsqlConnection(Db.ConnStr);
                await conn.OpenAsync();
                await using var cmd = new NpgsqlCommand("""
                    INSERT INTO workers(worker_id, node_name, cpu_percent, ram_mb, active_tasks, last_heartbeat)
                    VALUES (@w, @n, @c, @r, @a, NOW())
                    ON CONFLICT (worker_id) DO UPDATE
                    SET cpu_percent=@c, ram_mb=@r, active_tasks=@a, last_heartbeat=NOW()
                    """, conn);
                cmd.Parameters.AddWithValue("w", _workerId);
                cmd.Parameters.AddWithValue("n", _nodeName);
                cmd.Parameters.AddWithValue("c", cpuPct);
                cmd.Parameters.AddWithValue("r", ramMb);
                cmd.Parameters.AddWithValue("a", _active);
                await cmd.ExecuteNonQueryAsync();
            }
            catch { }
        }
    }
}