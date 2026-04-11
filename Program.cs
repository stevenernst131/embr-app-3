using System.Text.Json;
using Npgsql;
using Dapper;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

// Configure port from environment
var port = Environment.GetEnvironmentVariable("PORT") ?? "5000";
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

builder.Services.AddOpenApi();

// Database connection
var dbUrl = Environment.GetEnvironmentVariable("DATABASE_URL");
string? npgsqlConnStr = null;
if (!string.IsNullOrEmpty(dbUrl))
{
    var uri = new Uri(dbUrl);
    var userInfo = uri.UserInfo.Split(':');
    npgsqlConnStr = $"Host={uri.Host};Port={uri.Port};Database={uri.AbsolutePath.TrimStart('/')};Username={userInfo[0]};Password={userInfo[1]};SSL Mode=Disable;";
}
builder.Services.AddScoped<NpgsqlConnection>(_ => new NpgsqlConnection(npgsqlConnStr));

// Redis/Valkey cache connection
IConnectionMultiplexer? redis = null;
var cacheUrl = Environment.GetEnvironmentVariable("CACHE_URL");
if (!string.IsNullOrEmpty(cacheUrl))
{
    try
    {
        var cacheUri = new Uri(cacheUrl);
        var opts = new ConfigurationOptions
        {
            EndPoints = { { cacheUri.Host, cacheUri.Port } },
            AbortOnConnectFail = false,
            ConnectTimeout = 5000,
        };
        if (!string.IsNullOrEmpty(cacheUri.UserInfo))
        {
            var parts = cacheUri.UserInfo.Split(':');
            if (parts.Length == 2) opts.Password = Uri.UnescapeDataString(parts[1]);
        }
        redis = ConnectionMultiplexer.Connect(opts);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[WARN] Cache connection failed: {ex.Message}");
    }
}

// Blob storage config — try multiple env var names
var blobApiUrl = Environment.GetEnvironmentVariable("BLOB_STORE_URL") 
    ?? Environment.GetEnvironmentVariable("BLOB_URL")
    ?? Environment.GetEnvironmentVariable("EMBR_BLOB_URL");
var blobApiKey = Environment.GetEnvironmentVariable("BLOB_STORE_API_KEY") 
    ?? Environment.GetEnvironmentVariable("BLOB_API_KEY")
    ?? Environment.GetEnvironmentVariable("EMBR_BLOB_API_KEY");

var app = builder.Build();
app.MapOpenApi();

// Debug endpoint to discover env vars
app.MapGet("/debug/env", () =>
{
    var relevant = Environment.GetEnvironmentVariables()
        .Cast<System.Collections.DictionaryEntry>()
        .Where(e => {
            var k = e.Key.ToString()!.ToUpper();
            return k.Contains("BLOB") || k.Contains("DATABASE") || k.Contains("CACHE") 
                || k.Contains("PORT") || k.Contains("EMBR") || k.Contains("APP_");
        })
        .OrderBy(e => e.Key.ToString())
        .ToDictionary(
            e => e.Key.ToString()!,
            e => e.Key.ToString()!.Contains("PASSWORD") || e.Key.ToString()!.Contains("SECRET") || e.Key.ToString()!.Contains("KEY")
                ? "***REDACTED***" : e.Value?.ToString()
        );
    return Results.Ok(relevant);
});

var startTime = DateTime.UtcNow;
var appVersion = Environment.GetEnvironmentVariable("APP_VERSION") ?? "1.0.0";
var envName = Environment.GetEnvironmentVariable("EMBR_ENVIRONMENT") ?? "unknown";

// ===== HEALTH & STATUS =====
app.MapGet("/health", () => Results.Ok(new
{
    status = "healthy",
    version = appVersion,
    environment = envName,
    uptime = (DateTime.UtcNow - startTime).ToString(),
    timestamp = DateTime.UtcNow
}));

app.MapGet("/", () => Results.Ok(new
{
    app = "Embr Task Manager",
    version = appVersion,
    environment = envName,
    docs = "/openapi/v1.json",
    endpoints = new
    {
        health = "GET /health",
        status = "GET /status",
        projects = "GET /api/projects",
        tasks = "GET /api/projects/{id}/tasks",
        comments = "GET /api/tasks/{id}/comments",
        cache = "GET /api/cache/stats",
        blobs = "GET /api/blobs",
        search = "GET /api/search?q=term",
        dashboard = "GET /api/dashboard",
        activity = "GET /api/activity"
    }
}));

app.MapGet("/status", async (NpgsqlConnection db) =>
{
    var dbOk = false;
    string? dbError = null;
    try
    {
        await db.OpenAsync();
        await db.ExecuteScalarAsync("SELECT 1");
        dbOk = true;
    }
    catch (Exception ex) { dbError = ex.Message; }

    var cacheOk = redis?.IsConnected ?? false;

    return Results.Ok(new
    {
        status = "running",
        version = appVersion,
        environment = envName,
        uptime = (DateTime.UtcNow - startTime).ToString(),
        services = new
        {
            database = new { connected = dbOk, error = dbError },
            cache = new { connected = cacheOk, url = cacheUrl != null ? "configured" : "not configured" },
            blobs = new { configured = !string.IsNullOrEmpty(blobApiUrl) }
        }
    });
});

// ===== PROJECTS CRUD =====
app.MapGet("/api/projects", async (NpgsqlConnection db) =>
{
    await db.OpenAsync();
    var projects = await db.QueryAsync("SELECT * FROM projects ORDER BY created_at DESC");
    return Results.Ok(projects);
});

app.MapGet("/api/projects/{id}", async (int id, NpgsqlConnection db) =>
{
    await db.OpenAsync();
    // Check cache first
    if (redis?.IsConnected == true)
    {
        var cached = await redis.GetDatabase().StringGetAsync($"project:{id}");
        if (cached.HasValue)
            return Results.Ok(new { source = "cache", data = JsonSerializer.Deserialize<object>((string)cached!) });
    }
    var project = await db.QueryFirstOrDefaultAsync("SELECT * FROM projects WHERE id = @id", new { id });
    if (project == null) return Results.NotFound(new { error = "Project not found" });

    // Cache for 60 seconds
    if (redis?.IsConnected == true)
        await redis.GetDatabase().StringSetAsync($"project:{id}", JsonSerializer.Serialize(project), TimeSpan.FromSeconds(60));

    return Results.Ok(new { source = "database", data = project });
});

app.MapPost("/api/projects", async (HttpRequest req, NpgsqlConnection db) =>
{
    var body = await JsonSerializer.DeserializeAsync<JsonElement>(req.Body);
    var name = body.GetProperty("name").GetString();
    var description = body.TryGetProperty("description", out var d) ? d.GetString() : null;

    await db.OpenAsync();
    var project = await db.QueryFirstAsync(
        "INSERT INTO projects (name, description) VALUES (@name, @description) RETURNING *",
        new { name, description });

    await db.ExecuteAsync(
        "INSERT INTO activity_log (entity_type, entity_id, action, details) VALUES ('project', @id, 'created', @details::jsonb)",
        new { id = (int)project.id, details = JsonSerializer.Serialize(new { name }) });

    return Results.Created($"/api/projects/{project.id}", project);
});

app.MapPut("/api/projects/{id}", async (int id, HttpRequest req, NpgsqlConnection db) =>
{
    var body = await JsonSerializer.DeserializeAsync<JsonElement>(req.Body);
    await db.OpenAsync();
    var updates = new List<string>();
    var parameters = new DynamicParameters();
    parameters.Add("id", id);

    if (body.TryGetProperty("name", out var n)) { updates.Add("name = @name"); parameters.Add("name", n.GetString()); }
    if (body.TryGetProperty("description", out var desc)) { updates.Add("description = @desc"); parameters.Add("desc", desc.GetString()); }
    if (body.TryGetProperty("status", out var s)) { updates.Add("status = @status"); parameters.Add("status", s.GetString()); }
    updates.Add("updated_at = NOW()");

    var project = await db.QueryFirstOrDefaultAsync(
        $"UPDATE projects SET {string.Join(", ", updates)} WHERE id = @id RETURNING *", parameters);

    if (project == null) return Results.NotFound(new { error = "Project not found" });

    // Invalidate cache
    if (redis?.IsConnected == true) await redis.GetDatabase().KeyDeleteAsync($"project:{id}");

    return Results.Ok(project);
});

app.MapDelete("/api/projects/{id}", async (int id, NpgsqlConnection db) =>
{
    await db.OpenAsync();
    var rows = await db.ExecuteAsync("DELETE FROM projects WHERE id = @id", new { id });
    if (rows == 0) return Results.NotFound(new { error = "Project not found" });
    if (redis?.IsConnected == true) await redis.GetDatabase().KeyDeleteAsync($"project:{id}");
    return Results.Ok(new { deleted = true, id });
});

// ===== TASKS CRUD =====
app.MapGet("/api/projects/{projectId}/tasks", async (int projectId, string? status, string? priority, NpgsqlConnection db) =>
{
    await db.OpenAsync();
    var sql = "SELECT * FROM tasks WHERE project_id = @projectId";
    var parameters = new DynamicParameters();
    parameters.Add("projectId", projectId);
    if (!string.IsNullOrEmpty(status)) { sql += " AND status = @status"; parameters.Add("status", status); }
    if (!string.IsNullOrEmpty(priority)) { sql += " AND priority = @priority"; parameters.Add("priority", priority); }
    sql += " ORDER BY CASE priority WHEN 'critical' THEN 0 WHEN 'high' THEN 1 WHEN 'medium' THEN 2 WHEN 'low' THEN 3 END, created_at DESC";
    var tasks = await db.QueryAsync(sql, parameters);
    return Results.Ok(tasks);
});

app.MapPost("/api/projects/{projectId}/tasks", async (int projectId, HttpRequest req, NpgsqlConnection db) =>
{
    var body = await JsonSerializer.DeserializeAsync<JsonElement>(req.Body);
    var title = body.GetProperty("title").GetString();
    var description = body.TryGetProperty("description", out var d) ? d.GetString() : null;
    var priority = body.TryGetProperty("priority", out var p) ? p.GetString() : "medium";
    var assignee = body.TryGetProperty("assignee", out var a) ? a.GetString() : null;

    await db.OpenAsync();
    var task = await db.QueryFirstAsync(
        @"INSERT INTO tasks (project_id, title, description, priority, assignee) 
          VALUES (@projectId, @title, @description, @priority, @assignee) RETURNING *",
        new { projectId, title, description, priority, assignee });

    await db.ExecuteAsync(
        "INSERT INTO activity_log (entity_type, entity_id, action, details) VALUES ('task', @id, 'created', @details::jsonb)",
        new { id = (int)task.id, details = JsonSerializer.Serialize(new { title, projectId }) });

    return Results.Created($"/api/tasks/{task.id}", task);
});

app.MapGet("/api/tasks/{id}", async (int id, NpgsqlConnection db) =>
{
    await db.OpenAsync();
    var task = await db.QueryFirstOrDefaultAsync("SELECT * FROM tasks WHERE id = @id", new { id });
    if (task == null) return Results.NotFound(new { error = "Task not found" });
    return Results.Ok(task);
});

app.MapPut("/api/tasks/{id}", async (int id, HttpRequest req, NpgsqlConnection db) =>
{
    var body = await JsonSerializer.DeserializeAsync<JsonElement>(req.Body);
    await db.OpenAsync();
    var updates = new List<string>();
    var parameters = new DynamicParameters();
    parameters.Add("id", id);

    if (body.TryGetProperty("title", out var t)) { updates.Add("title = @title"); parameters.Add("title", t.GetString()); }
    if (body.TryGetProperty("description", out var d)) { updates.Add("description = @desc"); parameters.Add("desc", d.GetString()); }
    if (body.TryGetProperty("status", out var s)) { updates.Add("status = @status"); parameters.Add("status", s.GetString()); }
    if (body.TryGetProperty("priority", out var p)) { updates.Add("priority = @priority"); parameters.Add("priority", p.GetString()); }
    if (body.TryGetProperty("assignee", out var a)) { updates.Add("assignee = @assignee"); parameters.Add("assignee", a.GetString()); }
    updates.Add("updated_at = NOW()");

    var task = await db.QueryFirstOrDefaultAsync(
        $"UPDATE tasks SET {string.Join(", ", updates)} WHERE id = @id RETURNING *", parameters);
    if (task == null) return Results.NotFound(new { error = "Task not found" });

    await db.ExecuteAsync(
        "INSERT INTO activity_log (entity_type, entity_id, action, details) VALUES ('task', @id, 'updated', @details::jsonb)",
        new { id, details = JsonSerializer.Serialize(new { fields = updates }) });

    return Results.Ok(task);
});

app.MapDelete("/api/tasks/{id}", async (int id, NpgsqlConnection db) =>
{
    await db.OpenAsync();
    var rows = await db.ExecuteAsync("DELETE FROM tasks WHERE id = @id", new { id });
    if (rows == 0) return Results.NotFound(new { error = "Task not found" });
    return Results.Ok(new { deleted = true, id });
});

// ===== COMMENTS =====
app.MapGet("/api/tasks/{taskId}/comments", async (int taskId, NpgsqlConnection db) =>
{
    await db.OpenAsync();
    var comments = await db.QueryAsync("SELECT * FROM comments WHERE task_id = @taskId ORDER BY created_at", new { taskId });
    return Results.Ok(comments);
});

app.MapPost("/api/tasks/{taskId}/comments", async (int taskId, HttpRequest req, NpgsqlConnection db) =>
{
    var body = await JsonSerializer.DeserializeAsync<JsonElement>(req.Body);
    var author = body.GetProperty("author").GetString();
    var bodyText = body.GetProperty("body").GetString();

    await db.OpenAsync();
    var comment = await db.QueryFirstAsync(
        "INSERT INTO comments (task_id, author, body) VALUES (@taskId, @author, @body) RETURNING *",
        new { taskId, author, body = bodyText });

    return Results.Created($"/api/comments/{comment.id}", comment);
});

// ===== SEARCH =====
app.MapGet("/api/search", async (string q, NpgsqlConnection db) =>
{
    await db.OpenAsync();
    var pattern = $"%{q}%";
    var projects = await db.QueryAsync("SELECT id, name, 'project' as type FROM projects WHERE name ILIKE @pattern OR description ILIKE @pattern", new { pattern });
    var tasks = await db.QueryAsync("SELECT id, title as name, 'task' as type FROM tasks WHERE title ILIKE @pattern OR description ILIKE @pattern", new { pattern });
    return Results.Ok(new { query = q, results = projects.Concat(tasks) });
});

// ===== CACHE =====
app.MapGet("/api/cache/stats", () =>
{
    if (redis?.IsConnected != true)
        return Results.Ok(new { connected = false, message = "Cache not connected" });
    
    var server = redis.GetEndPoints().FirstOrDefault();
    return Results.Ok(new { connected = true, endpoint = server?.ToString() });
});

app.MapPost("/api/cache/set", async (HttpRequest req) =>
{
    if (redis?.IsConnected != true) return Results.BadRequest(new { error = "Cache not available" });
    var body = await JsonSerializer.DeserializeAsync<JsonElement>(req.Body);
    var key = body.GetProperty("key").GetString()!;
    var value = body.GetProperty("value").GetString()!;
    var ttl = body.TryGetProperty("ttl", out var t) ? t.GetInt32() : 300;
    await redis.GetDatabase().StringSetAsync(key, value, TimeSpan.FromSeconds(ttl));
    return Results.Ok(new { stored = true, key, ttl });
});

app.MapGet("/api/cache/get/{key}", async (string key) =>
{
    if (redis?.IsConnected != true) return Results.BadRequest(new { error = "Cache not available" });
    var value = await redis.GetDatabase().StringGetAsync(key);
    if (!value.HasValue) return Results.NotFound(new { key, found = false });
    return Results.Ok(new { key, value = value.ToString(), found = true });
});

// ===== BLOB STORAGE =====
app.MapGet("/api/blobs", async () =>
{
    if (string.IsNullOrEmpty(blobApiUrl) || string.IsNullOrEmpty(blobApiKey))
        return Results.Ok(new { configured = false, message = "Blob storage not configured" });

    using var http = new HttpClient();
    http.DefaultRequestHeaders.Add("x-api-key", blobApiKey);
    try
    {
        var resp = await http.GetAsync($"{blobApiUrl}/blobs");
        var body = await resp.Content.ReadAsStringAsync();
        return Results.Ok(new { configured = true, response = JsonSerializer.Deserialize<object>(body) });
    }
    catch (Exception ex)
    {
        return Results.Ok(new { configured = true, error = ex.Message });
    }
});

app.MapPost("/api/blobs/upload", async (HttpRequest req) =>
{
    if (string.IsNullOrEmpty(blobApiUrl) || string.IsNullOrEmpty(blobApiKey))
        return Results.BadRequest(new { error = "Blob storage not configured" });

    var body = await JsonSerializer.DeserializeAsync<JsonElement>(req.Body);
    var key = body.GetProperty("key").GetString()!;
    var content = body.GetProperty("content").GetString()!;

    using var http = new HttpClient();
    http.DefaultRequestHeaders.Add("x-api-key", blobApiKey);
    var payload = new StringContent(content, System.Text.Encoding.UTF8, "text/plain");
    var resp = await http.PutAsync($"{blobApiUrl}/blobs/{key}", payload);
    var result = await resp.Content.ReadAsStringAsync();
    return Results.Ok(new { uploaded = resp.IsSuccessStatusCode, key, response = result });
});

app.MapGet("/api/blobs/download/{key}", async (string key) =>
{
    if (string.IsNullOrEmpty(blobApiUrl) || string.IsNullOrEmpty(blobApiKey))
        return Results.BadRequest(new { error = "Blob storage not configured" });

    using var http = new HttpClient();
    http.DefaultRequestHeaders.Add("x-api-key", blobApiKey);
    var resp = await http.GetAsync($"{blobApiUrl}/blobs/{key}");
    if (!resp.IsSuccessStatusCode)
        return Results.NotFound(new { error = "Blob not found", key, statusCode = (int)resp.StatusCode });
    var content = await resp.Content.ReadAsStringAsync();
    return Results.Ok(new { key, content, contentType = resp.Content.Headers.ContentType?.ToString() });
});

// ===== DASHBOARD =====
app.MapGet("/api/dashboard", async (NpgsqlConnection db) =>
{
    await db.OpenAsync();
    var projectCount = await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM projects");
    var taskStats = await db.QueryFirstAsync(@"
        SELECT 
            COUNT(*) as total,
            COUNT(*) FILTER (WHERE status = 'todo') as todo,
            COUNT(*) FILTER (WHERE status = 'in_progress') as in_progress,
            COUNT(*) FILTER (WHERE status = 'done') as done,
            COUNT(*) FILTER (WHERE priority = 'critical') as critical,
            COUNT(*) FILTER (WHERE priority = 'high') as high
        FROM tasks");
    var recentActivity = await db.QueryAsync("SELECT * FROM activity_log ORDER BY created_at DESC LIMIT 10");

    return Results.Ok(new
    {
        projects = projectCount,
        tasks = taskStats,
        recent_activity = recentActivity,
        cache_connected = redis?.IsConnected ?? false,
        blobs_configured = !string.IsNullOrEmpty(blobApiUrl),
        uptime = (DateTime.UtcNow - startTime).ToString()
    });
});

// ===== ACTIVITY LOG =====
app.MapGet("/api/activity", async (int? limit, NpgsqlConnection db) =>
{
    await db.OpenAsync();
    var activities = await db.QueryAsync(
        "SELECT * FROM activity_log ORDER BY created_at DESC LIMIT @limit",
        new { limit = limit ?? 50 });
    return Results.Ok(activities);
});

// ===== BULK OPERATIONS =====
app.MapPost("/api/projects/{projectId}/tasks/bulk", async (int projectId, HttpRequest req, NpgsqlConnection db) =>
{
    var body = await JsonSerializer.DeserializeAsync<JsonElement>(req.Body);
    var tasks = body.GetProperty("tasks");
    await db.OpenAsync();
    var created = new List<object>();
    foreach (var t in tasks.EnumerateArray())
    {
        var title = t.GetProperty("title").GetString();
        var priority = t.TryGetProperty("priority", out var p) ? p.GetString() : "medium";
        var task = await db.QueryFirstAsync(
            "INSERT INTO tasks (project_id, title, priority) VALUES (@projectId, @title, @priority) RETURNING *",
            new { projectId, title, priority });
        created.Add(task);
    }
    return Results.Ok(new { created = created.Count, tasks = created });
});

Console.WriteLine($"🚀 Embr Task Manager v{appVersion} starting on port {port}");
Console.WriteLine($"📦 Environment: {envName}");
Console.WriteLine($"🗄️  Database: {(npgsqlConnStr != null ? "configured" : "not configured")}");
Console.WriteLine($"💾 Cache: {(redis?.IsConnected == true ? "connected" : "not connected")}");
Console.WriteLine($"📁 Blobs: {(!string.IsNullOrEmpty(blobApiUrl) ? "configured" : "not configured")}");

app.Run();
