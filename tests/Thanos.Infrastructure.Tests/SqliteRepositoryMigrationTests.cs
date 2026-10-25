using Microsoft.Data.Sqlite;
using Thanos.Core;
using Thanos.Infrastructure;

namespace Thanos.Infrastructure.Tests;

public sealed class SqliteRepositoryMigrationTests
{
    [Fact]
    public async Task Settings_round_trip_the_selected_theme()
    {
        var directory = Directory.CreateTempSubdirectory("thanos-settings-");
        try
        {
            var repository = new SqliteRepository(Path.Combine(directory.FullName, "settings.db"));
            await repository.InitializeAsync(TestContext.Current.CancellationToken);
            var expected = new AppSettings(.92f, false, DeleteMode.Permanent, false, AppTheme.Dark);

            await repository.SaveAsync(expected, TestContext.Current.CancellationToken);

            Assert.Equal(expected, await repository.LoadAsync(TestContext.Current.CancellationToken));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task Existing_single_result_set_is_preserved_as_scan_history()
    {
        var directory = Directory.CreateTempSubdirectory("thanos-migration-");
        var databasePath = Path.Combine(directory.FullName, "legacy.db");
        try
        {
            await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
            {
                await connection.OpenAsync(TestContext.Current.CancellationToken);
                var command = connection.CreateCommand();
                command.CommandText = """
                    PRAGMA foreign_keys=ON;
                    CREATE TABLE scan_folders (
                        id INTEGER PRIMARY KEY, full_path TEXT NOT NULL UNIQUE COLLATE NOCASE,
                        include_subfolders INTEGER NOT NULL, is_enabled INTEGER NOT NULL, last_scan_utc TEXT NULL);
                    CREATE TABLE photos (
                        id INTEGER PRIMARY KEY, root_folder_id INTEGER NOT NULL, full_path TEXT NOT NULL UNIQUE COLLATE NOCASE,
                        file_size INTEGER NOT NULL, last_write_utc TEXT NOT NULL, width INTEGER NULL, height INTEGER NULL,
                        content_hash TEXT NULL, embedding BLOB NULL, embedding_dimension INTEGER NULL,
                        embedding_model_version TEXT NULL, indexed_at_utc TEXT NULL, is_missing INTEGER NOT NULL DEFAULT 0,
                        sharpness REAL NOT NULL DEFAULT 0,
                        FOREIGN KEY(root_folder_id) REFERENCES scan_folders(id) ON DELETE CASCADE);
                    CREATE TABLE similarity_groups (
                        id TEXT PRIMARY KEY, representative_photo_id INTEGER NOT NULL, threshold_used REAL NOT NULL,
                        created_at_utc TEXT NOT NULL,
                        FOREIGN KEY(representative_photo_id) REFERENCES photos(id) ON DELETE CASCADE);
                    CREATE TABLE similarity_group_members (
                        group_id TEXT NOT NULL, photo_id INTEGER NOT NULL, similarity_score REAL NOT NULL,
                        PRIMARY KEY(group_id, photo_id),
                        FOREIGN KEY(group_id) REFERENCES similarity_groups(id) ON DELETE CASCADE,
                        FOREIGN KEY(photo_id) REFERENCES photos(id) ON DELETE CASCADE);
                    CREATE INDEX ix_similarity_groups_created ON similarity_groups(created_at_utc DESC);
                    INSERT INTO scan_folders VALUES(1, $folder, 1, 1, NULL);
                    INSERT INTO photos(id, root_folder_id, full_path, file_size, last_write_utc)
                        VALUES(1, 1, $one, 1, $written), (2, 1, $two, 1, $written);
                    INSERT INTO similarity_groups VALUES($group, 1, 0.9, $written);
                    INSERT INTO similarity_group_members VALUES($group, 1, 1.0), ($group, 2, 0.95);
                    """;
                command.Parameters.AddWithValue("$folder", directory.FullName);
                command.Parameters.AddWithValue("$one", Path.Combine(directory.FullName, "one.jpg"));
                command.Parameters.AddWithValue("$two", Path.Combine(directory.FullName, "two.jpg"));
                command.Parameters.AddWithValue("$group", Guid.NewGuid().ToString("D"));
                command.Parameters.AddWithValue("$written", DateTimeOffset.UtcNow.ToString("O"));
                await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }

            var repository = new SqliteRepository(databasePath);
            await repository.InitializeAsync(TestContext.Current.CancellationToken);
            var session = Assert.Single(await repository.GetScanSessionsAsync(TestContext.Current.CancellationToken));
            Assert.Equal(1, session.GroupCount);
            Assert.Equal(2, session.MatchedPhotoCount);
            Assert.Equal(2, Assert.Single(await repository.GetGroupsAsync(session.Id, TestContext.Current.CancellationToken)).MemberCount);

            await using var migrated = new SqliteConnection($"Data Source={databasePath}");
            await migrated.OpenAsync(TestContext.Current.CancellationToken);
            var indexQuery = migrated.CreateCommand();
            indexQuery.CommandText = "SELECT name FROM sqlite_master WHERE type='index' AND name LIKE 'ix_%'";
            var indexes = new List<string>();
            await using var reader = await indexQuery.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            while (await reader.ReadAsync(TestContext.Current.CancellationToken)) indexes.Add(reader.GetString(0));
            Assert.Contains("ix_scan_sessions_completed", indexes);
            Assert.Contains("ix_similarity_groups_session_created", indexes);
            Assert.DoesNotContain("ix_similarity_groups_created", indexes);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(true);
        }
    }
}
