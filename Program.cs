using Npgsql;
using System.Net.Http.Json;

var builder = WebApplication.CreateBuilder(args);

// Lê a Connection String do Render/Neon em produção; se não existir, usa o localhost para desenvolvimento local
string connString = builder.Configuration.GetConnectionString("DefaultConnection") 
    ?? "Host=localhost;Username=postgres;Password=emily2003;Database=meus_projetos";

string tmdbApiKey = "112e19db2a6ee988ed24f9024019d75b";

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

// Inicializa a tabela no PostgreSQL e adiciona coluna para tipo (Filme / Série)
using (var conn = new NpgsqlConnection(connString))
{
    conn.Open();
    using var cmd = new NpgsqlCommand(@"
        CREATE TABLE IF NOT EXISTS filmes (
            id SERIAL PRIMARY KEY,
            titulo VARCHAR(150),
            status VARCHAR(50),
            nota NUMERIC(3,1),
            comentario TEXT,
            poster_url TEXT,
            sinopse TEXT,
            ano VARCHAR(10),
            tipo VARCHAR(20) DEFAULT 'Filme'
        );
        ALTER TABLE filmes ADD COLUMN IF NOT EXISTS comentario TEXT;
        ALTER TABLE filmes ADD COLUMN IF NOT EXISTS poster_url TEXT;
        ALTER TABLE filmes ADD COLUMN IF NOT EXISTS sinopse TEXT;
        ALTER TABLE filmes ADD COLUMN IF NOT EXISTS ano VARCHAR(10);
        ALTER TABLE filmes ADD COLUMN IF NOT EXISTS tipo VARCHAR(20) DEFAULT 'Filme';
    ", conn);
    cmd.ExecuteNonQuery();
}

// PROXY DE BUSCA MULTI (Filmes + Séries)
app.MapGet("/api/buscar-filmes", async (string q) =>
{
    if (string.IsNullOrWhiteSpace(q) || q.Trim().Length < 2) 
        return Results.Ok(new List<object>());

    using var client = new HttpClient();
    client.Timeout = TimeSpan.FromSeconds(5);

    var url = $"https://api.themoviedb.org/3/search/multi?api_key={tmdbApiKey}&language=pt-BR&query={Uri.EscapeDataString(q.Trim())}&page=1";

    try
    {
        var response = await client.GetFromJsonAsync<System.Text.Json.JsonElement>(url);
        if (response.TryGetProperty("results", out var results) && results.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            var lista = new List<object>();
            foreach (var f in results.EnumerateArray())
            {
                string mediaType = f.TryGetProperty("media_type", out var mt) ? mt.GetString() ?? "" : "";
                
                if (mediaType != "movie" && mediaType != "tv") continue;

                string titulo = "";
                string releaseDate = "";
                string tipoRotulo = mediaType == "movie" ? "Filme" : "Série";

                if (mediaType == "movie")
                {
                    titulo = f.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                    releaseDate = f.TryGetProperty("release_date", out var r) ? r.GetString() ?? "" : "";
                }
                else if (mediaType == "tv")
                {
                    titulo = f.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    releaseDate = f.TryGetProperty("first_air_date", out var r) ? r.GetString() ?? "" : "";
                }

                string posterPath = f.TryGetProperty("poster_path", out var p) ? p.GetString() ?? "" : "";
                string poster = !string.IsNullOrEmpty(posterPath) ? $"https://image.tmdb.org/t/p/w500{posterPath}" : "";
                string ano = releaseDate.Length >= 4 ? releaseDate.Substring(0, 4) : "";
                string sinopse = f.TryGetProperty("overview", out var s) ? s.GetString() ?? "" : "";

                if (!string.IsNullOrEmpty(titulo))
                {
                    lista.Add(new { titulo, poster, ano, sinopse, tipo = tipoRotulo });
                }
            }
            return Results.Ok(lista.Take(8));
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[ERRO TMDB]: {ex.Message}");
    }

    return Results.Ok(new List<object>());
});

// GET: Listar filmes e séries salvos
app.MapGet("/api/filmes", () =>
{
    var filmes = new List<object>();
    using var conn = new NpgsqlConnection(connString);
    conn.Open();
    using var cmd = new NpgsqlCommand("SELECT id, titulo, status, nota, comentario, poster_url, sinopse, ano, tipo FROM filmes ORDER BY id DESC", conn);
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
    {
        filmes.Add(new {
            id = reader.GetInt32(0),
            titulo = reader.GetString(1),
            status = reader.GetString(2),
            nota = reader.GetDecimal(3),
            comentario = reader.IsDBNull(4) ? "" : reader.GetString(4),
            poster_url = reader.IsDBNull(5) ? "" : reader.GetString(5),
            sinopse = reader.IsDBNull(6) ? "" : reader.GetString(6),
            ano = reader.IsDBNull(7) ? "" : reader.GetString(7),
            tipo = reader.IsDBNull(8) ? "Filme" : reader.GetString(8)
        });
    }
    return Results.Ok(filmes);
});

// POST: Cadastrar filme ou série
app.MapPost("/api/filmes", (FilmeInput input) =>
{
    using var conn = new NpgsqlConnection(connString);
    conn.Open();
    using var cmd = new NpgsqlCommand(@"
        INSERT INTO filmes (titulo, status, nota, comentario, poster_url, sinopse, ano, tipo) 
        VALUES (@t, @s, @n, @c, @p, @syn, @a, @tp)", conn);
    cmd.Parameters.AddWithValue("t", input.Titulo);
    cmd.Parameters.AddWithValue("s", input.Status);
    cmd.Parameters.AddWithValue("n", input.Nota);
    cmd.Parameters.AddWithValue("c", (object?)input.Comentario ?? DBNull.Value);
    cmd.Parameters.AddWithValue("p", (object?)input.PosterUrl ?? DBNull.Value);
    cmd.Parameters.AddWithValue("syn", (object?)input.Sinopse ?? DBNull.Value);
    cmd.Parameters.AddWithValue("a", (object?)input.Ano ?? DBNull.Value);
    cmd.Parameters.AddWithValue("tp", (object?)input.Tipo ?? "Filme");
    cmd.ExecuteNonQuery();

    return Results.Ok(new { mensagem = "Guardado com sucesso!" });
});

// PUT: Atualizar
app.MapPut("/api/filmes/{id:int}", (int id, FilmeUpdateInput input) =>
{
    using var conn = new NpgsqlConnection(connString);
    conn.Open();
    using var cmd = new NpgsqlCommand(@"
        UPDATE filmes 
        SET status = @s, nota = @n, comentario = @c 
        WHERE id = @id", conn);
    cmd.Parameters.AddWithValue("id", id);
    cmd.Parameters.AddWithValue("s", input.Status);
    cmd.Parameters.AddWithValue("n", input.Nota);
    cmd.Parameters.AddWithValue("c", (object?)input.Comentario ?? DBNull.Value);

    int afetados = cmd.ExecuteNonQuery();
    if (afetados == 0) return Results.NotFound();

    return Results.Ok(new { mensagem = "Atualizado!" });
});

// DELETE: Remover
app.MapDelete("/api/filmes/{id:int}", (int id) =>
{
    using var conn = new NpgsqlConnection(connString);
    conn.Open();
    using var cmd = new NpgsqlCommand("DELETE FROM filmes WHERE id = @id", conn);
    cmd.Parameters.AddWithValue("id", id);
    cmd.ExecuteNonQuery();

    return Results.Ok(new { mensagem = "Eliminado!" });
});

app.Run();

record FilmeInput(string Titulo, string Status, decimal Nota, string? Comentario, string? PosterUrl, string? Sinopse, string? Ano, string? Tipo);
record FilmeUpdateInput(string Status, decimal Nota, string? Comentario);