# 板书时光机验收测试证据（历史归档）

> **【历史归档 · 2026-11-21 证据记录】** 本文件原记录时光机尚在本体内置阶段的 91 项验收用例。时光机已重构为独立插件模块「时光机」（Id `inkcanvas.time-machine`），用例归属已按重构调整：
>
> - **迁至插件测试项目**（`tests/InkCanvas.TimeMachine.Tests`，适配新命名空间与 `.icink` 课堂录制格式）：时间轴回放一致性、跨页 / 背景 / 擦除 / 撤销重做、随机拖动定位、倍速到达一致性、检查点定位、导出区间 / 倍速 / 仅板书 / 并发书写、真实 MP4 导出耗时等时间轴与导出用例。
> - **已删除，不迁入插件**：异常恢复与 journal 合并、`.sha256` 校验、`.bak` 备份回退、旧版会话迁移、录制落盘 / 恢复压测、讲解音频录制与音画同步、暂停 / 继续录制相关用例。
>
> 下列条目为历史留档，用例所在项目与类名可能已随重构变化，**不得作为当前测试基线引用**。插件侧新证据以插件测试项目结果为准。

## 历史用例归属对照（原 Classroom.Tests）

### 迁至插件：时间轴回放与定位

| 原用例 | 历史结果 | 重构归属 |
| --- | --- | --- |
| `EndToEndReplayTests.SyntheticSession_CrossPageBackgroundEraseClearMatchesEventOrder` | 通过 | 插件时间轴 |
| `EndToEndReplayTests.UndoRedo_RestoresStrokeInFrameOrder` | 通过 | 插件时间轴 |
| `EndToEndReplayTests.ClearedSecondPage_RemovesOnlyItsStrokesWhenReturning` | 通过 | 插件时间轴 |
| `EndToEndReplayTests.PptSlidePayload_EncodesSlideIdIndexAndEmptyScreenshot` | 通过 | 插件时间轴 |
| `EndToEndReplayTests.TimelineIndexAt_IsChronologicalAndMonotonic` | 通过 | 插件时间轴 |
| `EndToEndReplayTests.RepeatedBuildFrame_IsBitIdenticalAcrossFivePasses` | 通过 | 插件时间轴 |
| `EndToEndReplayTests.PlaybackSpeed_ArrivalFrameMatchesDirectSeek` | 通过 | 插件时间轴 |
| `EndToEndReplayTests.SpeedSwitching_KeepsIndexAtMonotonicAndFramesStable` | 通过 | 插件时间轴 |
| `EndToEndReplayTests.RandomSeek_Ascending/Descending/Shuffled_MatchesSequentialPlayback` | 通过 | 插件时间轴 |
| `TimeMachineTests.BuildFrame_*`（多页过滤 / 清空 / 擦除 / 撤销重做 / 背景跟随） | 通过 | 插件时间轴 |
| `TimeMachineTests.Checkpoint_GeneratedByCount_AndNearestIsCorrect` | 通过 | 插件时间轴 |
| `TimeMachineTests.BuildFrame_CheckpointIncrementalMatchesExpected` | 通过 | 插件时间轴 |
| `TimeAxisTests.SceneAt_RestoresPageAndBackgroundAtAnyTime` / `SceneAt_RestoresPptSlideRosterAndTimer` | 通过 | 插件时间轴 |
| `TimelineReliabilityTests.Normalize_*` / `Checkpoints_CarrySeq_*` / `CursorPoint_*` / `BuildFrame_DoesNotMutateEventStream` | 通过 | 插件时间轴 |
| `TimelineReliabilityTests.NinetyMinuteSession_SeekIsWithinOneSecond` | 通过 | 插件压测参考 |
| `LongPlaybackStressTests.BuildFrame_90MinutePlayback_30Fps_KeepsTimeAndMemoryBounded` | 通过 | 插件压测参考 |

### 迁至插件：导出

| 原用例 | 历史结果 | 重构归属 |
| --- | --- | --- |
| `EndToEndReplayTests.ExportSampling_At1x/At2x_MatchesBuildFrameFingerprint` | 通过 | 插件导出 |
| `EndToEndReplayTests.BoardOnlyExport_UsesSameStrokeSetAsFullClassroom` | 通过 | 插件导出 |
| `EndToEndReplayTests.ExportWhileWriting_MeetsStrokeLatencyBudget` | 通过 | 插件导出 |
| `TimeMachineAdvancedTests.SampleFrameTimes_1x/2x/4x/KeepsStartOffsetAndStaysWithinRange` | 通过 | 插件导出 |
| `TimeMachineAdvancedTests.SeekGate_CancelledTokenOrStaleVersion_DoesNotApply` | 通过 | 插件导出 |
| `TimeMachineAdvancedTests.StepTarget_ClampsAtBothEnds` | 通过 | 插件导出 |
| `ExportTimingTests.Export120SecondSyntheticSession_MeasuresRealMp4Timing` | 通过 | 插件导出 |
| `BatchCTests.ExportOptions_ValidateRejectsBadRangesAndPaths` / `SampleFrameTimes_RespectsFpsAndBounds` | 通过 | 插件导出 |

### 已删除：恢复 / 校验 / 备份 / 迁移 / 音频 / 暂停

| 原用例类别 | 代表用例 | 处置 |
| --- | --- | --- |
| 异常恢复与 journal 合并 | `RecordingThroughputTests.JournalAndStore_90Minutes_...`、`BatchCTests.Journal_*`、`ExportSyncTests.JournalRecovery_*` | 删除，不迁入插件 |
| 未完成会话检测 / 暂停继续 | `RecordingEnhancementTests.PendingJournalIds_*`、`Pause_*` / `Resume_*` / `Elapsed_*` | 删除（插件无暂停 / 继续、无启动恢复提示） |
| 完整性校验与备份回退 | `TimelineReliabilityTests.Save_WritesChecksumAndLoadVerifiesIt`、`TamperedFile_WithoutBackup_IsRejected`、`TamperedFile_WithBackup_FallsBackToBackup` | 删除 |
| 旧版会话迁移 | `TimelineReliabilityTests.LegacySessionWithoutVersion_GetsBackupAndUpgradedFields`、`TimeAxisTests.LegacyV1Session_*` / `SessionV2_*` | 删除 |
| 讲解音频与音画同步 | WASAPI 录制、WAV 重采样、AAC 音轨对齐、无音频设备降级等用例 | 删除（插件导出为纯视频，不含音轨） |

## 历史回归计数（2026-11-21，仅留档）

| 项目 | 历史结果 |
| --- | --- |
| `InkCanvas.Classroom.Tests` | 通过 269 / 269 |
| `InkCanvas.Ai.Tests` | 通过 132 / 132 |
| `InkCanvas.P0.Tests` | 通过 33 / 33 |
| `InkCanvas.Core.Tests` | 通过 148 / 148 |

上述计数为重构前基线，不代表当前回归状态；当前构建与测试结果以主执行者验收为准。
