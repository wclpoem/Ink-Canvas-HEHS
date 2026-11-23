using InkCanvas.Core.Classroom;
using InkCanvas.Features.Classroom;
using Xunit;

namespace InkCanvas.Classroom.Tests;

/// <summary>启动期异常恢复入口：扫描未完成日志、合并快照 + 增量日志、登记会话与放弃清理。</summary>
public sealed class SessionRecoveryTests
{
    [Fact]
    public void ScanPending_EmptyWhenNoJournal()
    {
        string dir = TempDir();
        try
        {
            Assert.Empty(ClassroomSessionRecovery.ScanPending(dir));
        }
        finally { DeleteTemp(dir); }
    }

    [Fact]
    public void ScanPending_ReportsJournalEventsAndRecoveredName()
    {
        string dir = TempDir();
        try
        {
            string id = Guid.NewGuid().ToString("N");
            using (var journal = SessionJournal.Open(id, dir, "未完成录制", DateTime.Now))
            {
                journal.Append(Ev(100));
                journal.Append(Ev(200));
                journal.Append(Ev(300));
                journal.Flush();
            }

            var pending = ClassroomSessionRecovery.ScanPending(dir);

            PendingRecording item = Assert.Single(pending);
            Assert.Equal(id, item.Id);
            Assert.Equal("未完成录制（已恢复）", item.Name);
            Assert.Equal(3, item.EventCount);
            Assert.Equal(300, item.LastEventTimeMs);
            Assert.Equal(TimeSpan.FromMilliseconds(300), item.PlaybackLength);
        }
        finally { DeleteTemp(dir); }
    }

    [Fact]
    public void Recover_MergesSnapshotWithJournalAndRegistersSession()
    {
        string dir = TempDir();
        try
        {
            string id = Guid.NewGuid().ToString("N");
            var snapshot = new ClassroomSession
            {
                Id = id,
                Name = "课堂录制",
                CreatedAt = new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Local),
                Events = new List<TimelineEvent> { Ev(100), Ev(200) },
            };
            ClassroomSessionStore.SaveSnapshot(snapshot, dir);
            using (var journal = SessionJournal.Open(id, dir, snapshot.Name, snapshot.CreatedAt))
            {
                journal.Append(Ev(100));
                journal.Append(Ev(200));
                journal.Append(Ev(300));
                journal.Append(Ev(400));
                journal.Flush();
            }

            ClassroomSession recovered = ClassroomSessionRecovery.Recover(id, dir);

            Assert.NotNull(recovered);
            Assert.Equal(id, recovered.Id);
            Assert.Equal("课堂录制（已恢复）", recovered.Name);
            Assert.Equal(4, recovered.Events.Count);
            Assert.Equal(new long[] { 100, 200, 300, 400 }, recovered.Events.Select(e => e.TimeMs));
            // 已登记为正式会话：列表可见，恢复残留被清理。
            Assert.True(File.Exists(ClassroomSessionStore.PathFor(id, dir)));
            Assert.Contains(ClassroomSessionStore.LoadAll(dir), s => s.Id == id);
            Assert.False(File.Exists(SessionJournal.PathFor(id, dir)));
            Assert.False(File.Exists(ClassroomSessionStore.PathForSnapshot(id, dir)));
            Assert.Empty(ClassroomSessionStore.PendingJournalIds(dir));
        }
        finally { DeleteTemp(dir); }
    }

    [Fact]
    public void Recover_WithoutSnapshot_UsesJournalOnly()
    {
        string dir = TempDir();
        try
        {
            string id = Guid.NewGuid().ToString("N");
            using (var journal = SessionJournal.Open(id, dir, "纯日志录制", DateTime.Now))
            {
                journal.Append(Ev(50));
                journal.Append(Ev(150));
                journal.Flush();
            }

            ClassroomSession recovered = ClassroomSessionRecovery.Recover(id, dir);

            Assert.NotNull(recovered);
            Assert.Equal(2, recovered.Events.Count);
            Assert.True(File.Exists(ClassroomSessionStore.PathFor(id, dir)));
            Assert.False(File.Exists(SessionJournal.PathFor(id, dir)));
        }
        finally { DeleteTemp(dir); }
    }

    [Fact]
    public void Recover_EmptyJournal_DoesNotRegisterEmptySession()
    {
        string dir = TempDir();
        try
        {
            string id = Guid.NewGuid().ToString("N");
            using (var journal = SessionJournal.Open(id, dir, "只有元数据", DateTime.Now)) journal.Flush();

            ClassroomSession recovered = ClassroomSessionRecovery.Recover(id, dir);

            Assert.Null(recovered);
            Assert.False(File.Exists(ClassroomSessionStore.PathFor(id, dir)));
            Assert.Empty(ClassroomSessionStore.PendingJournalIds(dir));
        }
        finally { DeleteTemp(dir); }
    }

    [Fact]
    public void Discard_RemovesRecoveryArtifactsButKeepsSavedSession()
    {
        string dir = TempDir();
        try
        {
            string id = Guid.NewGuid().ToString("N");
            var saved = new ClassroomSession
            {
                Id = id,
                Name = "已保存",
                CreatedAt = DateTime.Now,
                Events = new List<TimelineEvent> { Ev(10) },
            };
            ClassroomSessionStore.Save(saved, dir);
            ClassroomSessionStore.SaveSnapshot(saved, dir);
            using (var journal = SessionJournal.Open(id, dir, saved.Name, saved.CreatedAt))
            {
                journal.Append(Ev(20));
                journal.Flush();
            }

            Assert.True(ClassroomSessionRecovery.Discard(id, dir));

            Assert.True(File.Exists(ClassroomSessionStore.PathFor(id, dir)));
            Assert.False(File.Exists(SessionJournal.PathFor(id, dir)));
            Assert.False(File.Exists(ClassroomSessionStore.PathForSnapshot(id, dir)));
            Assert.False(ClassroomSessionRecovery.Discard(id, dir));
        }
        finally { DeleteTemp(dir); }
    }

    [Fact]
    public void RecoverAll_RegistersEveryPendingJournal()
    {
        string dir = TempDir();
        try
        {
            var ids = new List<string>();
            for (int i = 0; i < 2; i++)
            {
                string id = Guid.NewGuid().ToString("N");
                ids.Add(id);
                using (var journal = SessionJournal.Open(id, dir, "录制-" + i, DateTime.Now))
                {
                    journal.Append(Ev(100 + i));
                    journal.Flush();
                }
            }

            int recovered = ClassroomSessionRecovery.RecoverAll(ids, dir);

            Assert.Equal(2, recovered);
            Assert.All(ids, id => Assert.True(File.Exists(ClassroomSessionStore.PathFor(id, dir))));
            Assert.Empty(ClassroomSessionStore.PendingJournalIds(dir));
        }
        finally { DeleteTemp(dir); }
    }

    [Fact]
    public void DeleteExpiredSessions_KeepsMinimumRecentAndFreshSessions()
    {
        string dir = TempDir();
        try
        {
            var expired = Seed(dir, 5, new DateTime(2020, 1, 1, 8, 0, 0, DateTimeKind.Local));
            var fresh = Seed(dir, 25, DateTime.Now.AddMinutes(-30));

            int removed = ClassroomSessionStore.DeleteExpiredSessions(90, 20, dir);

            Assert.Equal(5, removed);
            Assert.All(expired, s => Assert.False(File.Exists(ClassroomSessionStore.PathFor(s.Id, dir))));
            Assert.All(fresh, s => Assert.True(File.Exists(ClassroomSessionStore.PathFor(s.Id, dir))));
        }
        finally { DeleteTemp(dir); }
    }

    [Fact]
    public void DeleteExpiredSessions_NeverDeletesMinimumKeepEvenWhenExpired()
    {
        string dir = TempDir();
        try
        {
            var expired = Seed(dir, 3, new DateTime(2020, 1, 1, 8, 0, 0, DateTimeKind.Local));

            Assert.Equal(0, ClassroomSessionStore.DeleteExpiredSessions(90, 3, dir));
            Assert.All(expired, s => Assert.True(File.Exists(ClassroomSessionStore.PathFor(s.Id, dir))));
        }
        finally { DeleteTemp(dir); }
    }

    [Fact]
    public void DeleteExpiredSessions_KeepsSessionsWithPendingJournalForRecovery()
    {
        string dir = TempDir();
        try
        {
            var expired = Seed(dir, 1, new DateTime(2020, 1, 1, 8, 0, 0, DateTimeKind.Local));
            ClassroomSession pending = expired[0];
            using (var journal = SessionJournal.Open(pending.Id, dir, pending.Name, pending.CreatedAt))
            {
                journal.Append(Ev(10));
                journal.Flush();
            }

            Assert.Equal(0, ClassroomSessionStore.DeleteExpiredSessions(90, 0, dir));
            Assert.True(File.Exists(ClassroomSessionStore.PathFor(pending.Id, dir)));
            Assert.True(File.Exists(SessionJournal.PathFor(pending.Id, dir)));
        }
        finally { DeleteTemp(dir); }
    }

    private static TimelineEvent Ev(long timeMs)
        => new() { TimeMs = timeMs, PageIndex = 0, Kind = TimelineEventKind.PageChanged };

    private static List<ClassroomSession> Seed(string dir, int count, DateTime start)
    {
        var sessions = new List<ClassroomSession>();
        for (int i = 0; i < count; i++)
        {
            var session = new ClassroomSession
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = "seed-" + i,
                CreatedAt = start.AddMinutes(i),
                Events = new List<TimelineEvent> { Ev(i) },
            };
            ClassroomSessionStore.Save(session, dir);
            sessions.Add(session);
        }
        return sessions;
    }

    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ink-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void DeleteTemp(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { /* 测试清理失败不影响结果 */ }
    }
}