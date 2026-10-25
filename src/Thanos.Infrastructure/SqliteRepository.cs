using Microsoft.Data.Sqlite;
using Thanos.Core;

namespace Thanos.Infrastructure;

public sealed class SqliteRepository : IScanRepository, ISettingsRepository
{
    private readonly string _connectionString;

    public SqliteRepository(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = true
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA busy_timeout=5000;
            CREATE TABLE IF NOT EXISTS scan_folders (
                id INTEGER PRIMARY KEY,
                full_path TEXT NOT NULL UNIQUE COLLATE NOCASE,
                include_subfolders INTEGER NOT NULL DEFAULT 1,
                is_enabled INTEGER NOT NULL DEFAULT 1,
                last_scan_utc TEXT NULL
            );
            CREATE TABLE IF NOT EXISTS photos (
                id INTEGER PRIMARY KEY,
                root_folder_id INTEGER NOT NULL,
                full_path TEXT NOT NULL UNIQUE COLLATE NOCASE,
                file_size INTEGER NOT NULL,
                last_write_utc TEXT NOT NULL,
                width INTEGER NULL,
                height INTEGER NULL,
                content_hash TEXT NULL,
                embedding BLOB NULL,
                embedding_dimension INTEGER NULL,
                embedding_model_version TEXT NULL,
                indexed_at_utc TEXT NULL,
                is_missing INTEGER NOT NULL DEFAULT 0,
                sharpness REAL NOT NULL DEFAULT 0,
                FOREIGN KEY(root_folder_id) REFERENCES scan_folders(id) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS ix_photos_root_folder ON photos(root_folder_id);
            CREATE INDEX IF NOT EXISTS ix_photos_content_hash ON photos(content_hash) WHERE content_hash IS NOT NULL;
            CREATE TABLE IF NOT EXISTS scan_sessions (
                id TEXT PRIMARY KEY,
                completed_at_utc TEXT NOT NULL,
                folder_summary TEXT NOT NULL,
                folder_count INTEGER NOT NULL,
                group_count INTEGER NOT NULL,
                matched_photo_count INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS similarity_groups (
                id TEXT PRIMARY KEY,
                scan_session_id TEXT NOT NULL,
                representative_photo_id INTEGER NOT NULL,
                threshold_used REAL NOT NULL,
                created_at_utc TEXT NOT NULL,
                FOREIGN KEY(scan_session_id) REFERENCES scan_sessions(id) ON DELETE CASCADE,
                FOREIGN KEY(representative_photo_id) REFERENCES photos(id) ON DELETE CASCADE
            );
            CREATE TABLE IF NOT EXISTS similarity_group_members (
                group_id TEXT NOT NULL,
                photo_id INTEGER NOT NULL,
                similarity_score REAL NOT NULL,
                PRIMARY KEY(group_id, photo_id),
                FOREIGN KEY(group_id) REFERENCES similarity_groups(id) ON DELETE CASCADE,
                FOREIGN KEY(photo_id) REFERENCES photos(id) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS ix_group_members_photo ON similarity_group_members(photo_id);
            CREATE TABLE IF NOT EXISTS app_settings (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        await EnsureScanSessionSchemaAsync(connection, cancellationToken);
    }

    public async Task<IReadOnlyList<ScanFolder>> GetFoldersAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT id, full_path, include_subfolders, is_enabled, last_scan_utc FROM scan_folders ORDER BY full_path";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<ScanFolder>();
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetBoolean(2), reader.GetBoolean(3), ReadDate(reader, 4)));
        return result;
    }

    public async Task<ScanFolder> UpsertFolderAsync(string path, bool includeSubfolders, CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!Directory.Exists(fullPath)) throw new DirectoryNotFoundException(fullPath);
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO scan_folders(full_path, include_subfolders, is_enabled)
            VALUES($path, $subfolders, 1)
            ON CONFLICT(full_path) DO UPDATE SET include_subfolders=excluded.include_subfolders, is_enabled=1
            RETURNING id, full_path, include_subfolders, is_enabled, last_scan_utc;
            """;
        command.Parameters.AddWithValue("$path", fullPath);
        command.Parameters.AddWithValue("$subfolders", includeSubfolders);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return new(reader.GetInt64(0), reader.GetString(1), reader.GetBoolean(2), reader.GetBoolean(3), ReadDate(reader, 4));
    }

    public async Task RemoveFolderAsync(long folderId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM scan_folders WHERE id=$id";
        command.Parameters.AddWithValue("$id", folderId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SetFolderStateAsync(long folderId, bool includeSubfolders, bool isEnabled, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "UPDATE scan_folders SET include_subfolders=$subfolders, is_enabled=$enabled WHERE id=$id";
        command.Parameters.AddWithValue("$subfolders", includeSubfolders);
        command.Parameters.AddWithValue("$enabled", isEnabled);
        command.Parameters.AddWithValue("$id", folderId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SetFolderScanCompletedAsync(long folderId, DateTimeOffset completedAt, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "UPDATE scan_folders SET last_scan_utc=$completed WHERE id=$id";
        command.Parameters.AddWithValue("$completed", completedAt.ToString("O"));
        command.Parameters.AddWithValue("$id", folderId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> CountPhotosAsync(long folderId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM photos WHERE root_folder_id=$id AND is_missing=0";
        command.Parameters.AddWithValue("$id", folderId);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    public async Task<Photo?> FindPhotoAsync(string fullPath, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = PhotoSelect + " WHERE p.full_path=$path";
        command.Parameters.AddWithValue("$path", Path.GetFullPath(fullPath));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadPhoto(reader) : null;
    }

    public async Task<IReadOnlyList<Photo>> GetIndexedPhotosAsync(IReadOnlyCollection<long> rootFolderIds, CancellationToken cancellationToken)
    {
        if (rootFolderIds.Count == 0) return [];
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        var parameters = rootFolderIds.Distinct().Select((id, index) => (id, name: $"$folder{index}")).ToArray();
        command.CommandText = PhotoSelect + $" WHERE p.root_folder_id IN ({string.Join(",", parameters.Select(x => x.name))}) AND p.is_missing=0 AND p.embedding IS NOT NULL ORDER BY p.id";
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.name, parameter.id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<Photo>();
        while (await reader.ReadAsync(cancellationToken)) result.Add(ReadPhoto(reader));
        return result;
    }

    public async Task<Photo> UpsertPhotoAsync(Photo photo, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO photos(root_folder_id, full_path, file_size, last_write_utc, width, height, content_hash,
                               embedding, embedding_dimension, embedding_model_version, indexed_at_utc, is_missing, sharpness)
            VALUES($root, $path, $size, $written, $width, $height, $hash, $embedding, $dimension, $version, $indexed, 0, $sharpness)
            ON CONFLICT(full_path) DO UPDATE SET
                root_folder_id=excluded.root_folder_id, file_size=excluded.file_size, last_write_utc=excluded.last_write_utc,
                width=excluded.width, height=excluded.height, content_hash=excluded.content_hash,
                embedding=excluded.embedding, embedding_dimension=excluded.embedding_dimension,
                embedding_model_version=excluded.embedding_model_version, indexed_at_utc=excluded.indexed_at_utc,
                is_missing=0, sharpness=excluded.sharpness
            RETURNING id;
            """;
        command.Parameters.AddWithValue("$root", photo.RootFolderId);
        command.Parameters.AddWithValue("$path", Path.GetFullPath(photo.FullPath));
        command.Parameters.AddWithValue("$size", photo.FileSize);
        command.Parameters.AddWithValue("$written", photo.LastWriteUtc.ToString("O"));
        command.Parameters.AddWithValue("$width", (object?)photo.Width ?? DBNull.Value);
        command.Parameters.AddWithValue("$height", (object?)photo.Height ?? DBNull.Value);
        command.Parameters.AddWithValue("$hash", (object?)photo.ContentHash ?? DBNull.Value);
        command.Parameters.AddWithValue("$embedding", photo.Embedding is null ? DBNull.Value : ToBytes(photo.Embedding));
        command.Parameters.AddWithValue("$dimension", (object?)photo.Embedding?.Length ?? DBNull.Value);
        command.Parameters.AddWithValue("$version", (object?)photo.EmbeddingModelVersion ?? DBNull.Value);
        command.Parameters.AddWithValue("$indexed", photo.IndexedAtUtc is null ? DBNull.Value : photo.IndexedAtUtc.Value.ToString("O"));
        command.Parameters.AddWithValue("$sharpness", photo.Sharpness);
        var id = (long)(await command.ExecuteScalarAsync(cancellationToken) ?? throw new InvalidOperationException("Photo upsert returned no id."));
        return photo with { Id = id, FullPath = Path.GetFullPath(photo.FullPath) };
    }

    public async Task MarkMissingAsync(long rootFolderId, IReadOnlyCollection<string> existingPaths, CancellationToken cancellationToken)
    {
        var existing = existingPaths.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        var select = connection.CreateCommand();
        select.Transaction = transaction;
        select.CommandText = "SELECT id, full_path FROM photos WHERE root_folder_id=$root AND is_missing=0";
        select.Parameters.AddWithValue("$root", rootFolderId);
        var missing = new List<long>();
        await using (var reader = await select.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken))
                if (!existing.Contains(reader.GetString(1))) missing.Add(reader.GetInt64(0));

        foreach (var id in missing)
        {
            var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE photos SET is_missing=1 WHERE id=$id";
            update.Parameters.AddWithValue("$id", id);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task SaveScanSessionAsync(
        ScanSession session,
        IReadOnlyCollection<SimilarityGroup> groups,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        var insertSession = connection.CreateCommand();
        insertSession.Transaction = transaction;
        insertSession.CommandText = """
            INSERT INTO scan_sessions(id, completed_at_utc, folder_summary, folder_count, group_count, matched_photo_count)
            VALUES($id, $completed, $folders, $folderCount, $groupCount, $matchedCount)
            """;
        insertSession.Parameters.AddWithValue("$id", session.Id.ToString("D"));
        insertSession.Parameters.AddWithValue("$completed", session.CompletedAtUtc.ToString("O"));
        insertSession.Parameters.AddWithValue("$folders", session.FolderSummary);
        insertSession.Parameters.AddWithValue("$folderCount", session.FolderCount);
        insertSession.Parameters.AddWithValue("$groupCount", session.GroupCount);
        insertSession.Parameters.AddWithValue("$matchedCount", session.MatchedPhotoCount);
        await insertSession.ExecuteNonQueryAsync(cancellationToken);

        foreach (var group in groups.Where(x => x.MemberCount >= 2))
        {
            var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO similarity_groups(id, scan_session_id, representative_photo_id, threshold_used, created_at_utc)
                VALUES($id, $session, $representative, $threshold, $created)
                """;
            insert.Parameters.AddWithValue("$id", group.Id.ToString("D"));
            insert.Parameters.AddWithValue("$session", session.Id.ToString("D"));
            insert.Parameters.AddWithValue("$representative", group.RepresentativePhotoId);
            insert.Parameters.AddWithValue("$threshold", group.ThresholdUsed);
            insert.Parameters.AddWithValue("$created", group.CreatedAtUtc.ToString("O"));
            await insert.ExecuteNonQueryAsync(cancellationToken);
            foreach (var member in group.Members)
            {
                var memberInsert = connection.CreateCommand();
                memberInsert.Transaction = transaction;
                memberInsert.CommandText = "INSERT INTO similarity_group_members(group_id, photo_id, similarity_score) VALUES($group,$photo,$score)";
                memberInsert.Parameters.AddWithValue("$group", group.Id.ToString("D"));
                memberInsert.Parameters.AddWithValue("$photo", member.Photo.Id);
                memberInsert.Parameters.AddWithValue("$score", member.SimilarityScore);
                await memberInsert.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        var trim = connection.CreateCommand();
        trim.Transaction = transaction;
        trim.CommandText = """
            DELETE FROM scan_sessions
            WHERE id IN (
                SELECT id FROM scan_sessions
                ORDER BY completed_at_utc DESC, id DESC
                LIMIT -1 OFFSET 5
            )
            """;
        await trim.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ScanSession>> GetScanSessionsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            WITH visible_groups AS (
                SELECT g.scan_session_id, g.id, COUNT(*) AS matched_photos
                FROM similarity_groups g
                JOIN similarity_group_members m ON m.group_id=g.id
                JOIN photos p ON p.id=m.photo_id
                WHERE p.is_missing=0
                GROUP BY g.scan_session_id, g.id
                HAVING COUNT(*) >= 2
            ), session_stats AS (
                SELECT scan_session_id, COUNT(*) AS group_count, SUM(matched_photos) AS matched_photo_count
                FROM visible_groups
                GROUP BY scan_session_id
            )
            SELECT s.id, s.completed_at_utc, s.folder_summary, s.folder_count,
                   COALESCE(stats.group_count, 0), COALESCE(stats.matched_photo_count, 0)
            FROM scan_sessions s
            LEFT JOIN session_stats stats ON stats.scan_session_id=s.id
            ORDER BY s.completed_at_utc DESC, s.id DESC
            LIMIT 5
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<ScanSession>();
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new(Guid.Parse(reader.GetString(0)), DateTimeOffset.Parse(reader.GetString(1)), reader.GetString(2),
                reader.GetInt32(3), reader.GetInt32(4), reader.GetInt32(5)));
        return result;
    }

    public async Task<IReadOnlyList<SimilarityGroup>> GetGroupsAsync(Guid scanSessionId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT g.id, g.representative_photo_id, g.threshold_used, g.created_at_utc,
                   p.id, p.root_folder_id, p.full_path, p.file_size, p.last_write_utc, p.width, p.height,
                   p.content_hash, p.embedding, p.embedding_model_version, p.indexed_at_utc, p.is_missing, p.sharpness,
                   m.similarity_score
            FROM similarity_groups g
            JOIN similarity_group_members m ON m.group_id=g.id
            JOIN photos p ON p.id=m.photo_id
            WHERE g.scan_session_id=$session AND p.is_missing=0
            ORDER BY g.created_at_utc DESC, g.id, m.similarity_score DESC;
            """;
        command.Parameters.AddWithValue("$session", scanSessionId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<(Guid GroupId, long RepresentativeId, float Threshold, DateTimeOffset Created, Photo Photo, float Score)>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var photo = ReadPhoto(reader, 4);
            rows.Add((Guid.Parse(reader.GetString(0)), reader.GetInt64(1), reader.GetFloat(2), DateTimeOffset.Parse(reader.GetString(3)), photo, reader.GetFloat(17)));
        }
        return rows.GroupBy(x => x.GroupId)
            .Select(x =>
            {
                var members = x.Select(row => new SimilarityGroupMember(row.Photo, row.Score)).ToArray();
                var storedRepresentative = x.First().RepresentativeId;
                var representative = members.Any(member => member.Photo.Id == storedRepresentative)
                    ? storedRepresentative
                    : SimilarityGroupingService.ChooseRepresentative(members.Select(member => member.Photo)).Id;
                return new SimilarityGroup(x.Key, representative, members, x.First().Threshold, x.First().Created);
            })
            .Where(x => x.MemberCount >= 2)
            .OrderByDescending(x => x.MemberCount)
            .ToArray();
    }

    public async Task SetGroupRepresentativeAsync(Guid groupId, long photoId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE similarity_groups
            SET representative_photo_id=$photo
            WHERE id=$group
              AND EXISTS (SELECT 1 FROM similarity_group_members WHERE group_id=$group AND photo_id=$photo)
            """;
        command.Parameters.AddWithValue("$group", groupId.ToString("D"));
        command.Parameters.AddWithValue("$photo", photoId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException("The recommended photo must belong to the group.");
    }

    public async Task RemoveGroupAsync(Guid groupId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE scan_sessions
            SET group_count=MAX(0, group_count - 1),
                matched_photo_count=MAX(0, matched_photo_count - COALESCE(
                    (SELECT COUNT(*) FROM similarity_group_members WHERE group_id=$id), 0))
            WHERE id=(SELECT scan_session_id FROM similarity_groups WHERE id=$id);
            DELETE FROM similarity_groups WHERE id=$id;
            """;
        command.Parameters.AddWithValue("$id", groupId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task RemovePhotosAsync(IReadOnlyCollection<string> paths, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        foreach (var path in paths)
        {
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM photos WHERE full_path=$path";
            command.Parameters.AddWithValue("$path", Path.GetFullPath(path));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        var refreshCounts = connection.CreateCommand();
        refreshCounts.Transaction = transaction;
        refreshCounts.CommandText = """
            DELETE FROM similarity_groups
            WHERE (SELECT COUNT(*) FROM similarity_group_members WHERE group_id=similarity_groups.id) < 2;
            UPDATE scan_sessions
            SET group_count=(SELECT COUNT(*) FROM similarity_groups g WHERE g.scan_session_id=scan_sessions.id),
                matched_photo_count=(
                    SELECT COUNT(*)
                    FROM similarity_group_members m
                    JOIN similarity_groups g ON g.id=m.group_id
                    WHERE g.scan_session_id=scan_sessions.id
                );
            """;
        await refreshCounts.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT key, value FROM app_settings";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(cancellationToken)) values[reader.GetString(0)] = reader.GetString(1);
        return new(
            values.TryGetValue("threshold", out var threshold) && float.TryParse(threshold, System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : .90f,
            !values.TryGetValue("gpu", out var gpu) || bool.Parse(gpu),
            values.TryGetValue("deleteMode", out var mode) && Enum.TryParse<DeleteMode>(mode, out var parsedMode) ? parsedMode : DeleteMode.RecycleBin,
            !values.TryGetValue("confirmDelete", out var confirm) || bool.Parse(confirm),
            values.TryGetValue("theme", out var theme) && Enum.TryParse<AppTheme>(theme, out var parsedTheme) ? parsedTheme : AppTheme.System);
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        var values = new Dictionary<string, string>
        {
            ["threshold"] = settings.SimilarityThreshold.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["gpu"] = settings.UseGpu.ToString(),
            ["deleteMode"] = settings.DeleteMode.ToString(),
            ["confirmDelete"] = settings.ConfirmBeforeDelete.ToString(),
            ["theme"] = settings.Theme.ToString()
        };
        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        foreach (var pair in values)
        {
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO app_settings(key,value) VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value";
            command.Parameters.AddWithValue("$key", pair.Key);
            command.Parameters.AddWithValue("$value", pair.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task EnsureScanSessionSchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var columns = connection.CreateCommand();
        columns.CommandText = "PRAGMA table_info(similarity_groups)";
        var hasSessionColumn = false;
        await using (var reader = await columns.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken))
                hasSessionColumn |= string.Equals(reader.GetString(1), "scan_session_id", StringComparison.OrdinalIgnoreCase);

        if (!hasSessionColumn)
        {
            var alter = connection.CreateCommand();
            alter.CommandText = """
                ALTER TABLE similarity_groups
                ADD COLUMN scan_session_id TEXT NULL REFERENCES scan_sessions(id) ON DELETE CASCADE
                """;
            await alter.ExecuteNonQueryAsync(cancellationToken);

            var legacySessionId = Guid.Empty.ToString("D");
            var migrate = connection.CreateCommand();
            migrate.CommandText = """
                INSERT OR IGNORE INTO scan_sessions(
                    id, completed_at_utc, folder_summary, folder_count, group_count, matched_photo_count)
                SELECT $session,
                       COALESCE(MAX(g.created_at_utc), $now),
                       'Previous scan',
                       (SELECT COUNT(*) FROM scan_folders WHERE is_enabled=1),
                       COUNT(DISTINCT g.id),
                       COUNT(m.photo_id)
                FROM similarity_groups g
                LEFT JOIN similarity_group_members m ON m.group_id=g.id
                HAVING COUNT(DISTINCT g.id) > 0;
                UPDATE similarity_groups SET scan_session_id=$session WHERE scan_session_id IS NULL;
                """;
            migrate.Parameters.AddWithValue("$session", legacySessionId);
            migrate.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            await migrate.ExecuteNonQueryAsync(cancellationToken);
        }

        var indexes = connection.CreateCommand();
        indexes.CommandText = """
            DROP INDEX IF EXISTS ix_similarity_groups_created;
            CREATE INDEX IF NOT EXISTS ix_scan_sessions_completed
                ON scan_sessions(completed_at_utc DESC, id DESC);
            CREATE INDEX IF NOT EXISTS ix_similarity_groups_session_created
                ON similarity_groups(scan_session_id, created_at_utc DESC);
            """;
        await indexes.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000";
        await pragma.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    private const string PhotoSelect = """
        SELECT p.id, p.root_folder_id, p.full_path, p.file_size, p.last_write_utc, p.width, p.height,
               p.content_hash, p.embedding, p.embedding_model_version, p.indexed_at_utc, p.is_missing, p.sharpness
        FROM photos p
        """;

    private static Photo ReadPhoto(SqliteDataReader reader, int offset = 0) => new(
        reader.GetInt64(offset), reader.GetInt64(offset + 1), reader.GetString(offset + 2), reader.GetInt64(offset + 3),
        DateTimeOffset.Parse(reader.GetString(offset + 4)),
        reader.IsDBNull(offset + 5) ? null : reader.GetInt32(offset + 5),
        reader.IsDBNull(offset + 6) ? null : reader.GetInt32(offset + 6),
        reader.IsDBNull(offset + 7) ? null : reader.GetString(offset + 7),
        reader.IsDBNull(offset + 8) ? null : FromBytes((byte[])reader[offset + 8]),
        reader.IsDBNull(offset + 9) ? null : reader.GetString(offset + 9),
        reader.IsDBNull(offset + 10) ? null : DateTimeOffset.Parse(reader.GetString(offset + 10)),
        reader.GetBoolean(offset + 11), reader.GetDouble(offset + 12));

    private static DateTimeOffset? ReadDate(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : DateTimeOffset.Parse(reader.GetString(ordinal));

    private static byte[] ToBytes(float[] values)
    {
        var bytes = new byte[values.Length * sizeof(float)];
        Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static float[] FromBytes(byte[] bytes)
    {
        var values = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, values, 0, bytes.Length);
        return values;
    }
}
