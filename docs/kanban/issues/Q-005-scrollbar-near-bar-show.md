---
id: Q-005
title: 滚动条显示条件收窄——指针活动改为邻近命中区才显示
type: feature
priority: P2
size: S
status: done
created: 2026-10-24
updated: 2026-10-24
blocked: none
---

## Goal

滚动条显示来源从「滚动值变化 或 **指针在滚动区内活动**」收窄为「滚动值变化 或 **指针移入滚动条邻近区**」，对齐用户原始预期："滚动时显示 / 鼠标移到滚动条附近时显示"——鼠标在内容区中部活动不再唤醒滚动条。

## Background

- 用户反馈（2026-10-24 会话）：记忆中的规格是"鼠标移到滚动条附近才显示"，但真机表现是"鼠标移到受滚动影响的区域就显示"。取证确认实现与 Q-004 卡片规格一致（"指针在滚动区内活动即显"，TC-005 固化）——是 Q-004 建卡时的设计偏离了用户预期，非回归。
- Q-004 卡片记录的原始用户反馈只有三点（常驻 / 不贴边 / 有箭头），"指针在滚动区内活动即显"是实施时定下的触发语义；本卡按用户 2026-10-24 拍板收窄，**取代 Q-004 验收标准第 3 条**（Q-004 卡作为历史记录不改）。
- 实现取舍：显示门控从"宿主 ScrollViewer.PointerMoved 无条件 Show()"改为纯几何邻近判定——命中条矩形（10px 宽）经宿主坐标系外扩 `ScrollBarOverlay.NearDistance = 24` 后包含指针位置才 Show。不依赖命中测试，隐藏态 Opacity 0 + IsHitTestVisible=False 也照样可"接近"；横竖滚动条 / TextBox / 下拉内部各自独立判定（每个 ScrollBar 一份 Behavior，机制不变）。

## Acceptance Criteria

- [x] 指针在内容区中部活动（远离滚动条）滚动条不显示
- [x] 指针移入邻近区（命中条外扩 24px，即距视口右缘 34px 内）即显；不依赖直接命中（区内、条外即可）
- [x] 滚动值变化即显、闲置延迟后淡出——Q-004 语义不变
- [x] 悬停命中条/拖拽滑块钉住不隐——Q-004 语义不变
- [x] 全量 `dotnet test Tokenmetry.sln` 全绿

## Subtasks

- [x] `Controls/ScrollBarOverlay`：`OnOwnerPointerMoved` 加 `IsPointerNearBar` 门控（命中条矩形 `Rect.Inflate(NearDistance)` 后 `Contains` 指针位置）；`NearDistance` 常量公开供测试/调参照
- [x] `ScrollBarOverlayTests`：TC-005 翻转为两条——远离不显示 / 移近即显且闲置收回；文件头契约注释同步
- [x] `docs/design/design-tokens.md` §9 语义描述更新

## Dependencies

- Q-004（前置：overlay 显隐状态机即本卡改动的载体）

## Test Cases

### TC-001~007: headless 契约（ScrollBarOverlayTests）

原 6 条中 TC-005（指针活动即显）拆翻为两条：

- `Pointer_activity_far_from_bar_does_not_show_it`：内容区中部两处移动 → `overlay-shown` 不出现（本卡判别性用例，RED 实锤：旧实现下该类被加上）
- `Pointer_nearing_bar_shows_it_then_idle_hides_it`：指针移到条左侧 20px 处（区内、条外）→ 显示；注入 1ms 闲置窗 + 新活动 → 收回

其余（闲置隐藏 / 无箭头 / 贴边×2 / 滚动即显闲置即隐 / 悬停钉住）不受影响原样绿。

### 真机人工核验（headless 能力外）

- 24px 邻近窗的手感：右缘 34px 内接近即显是否过早/过晚，横条同理（底部）。
- 明暗 × 1x/2x 下重复 Q-004 的目检路径，确认内容区活动不再唤醒滚动条。

## Development Log

### 2026-10-24（建卡即实施，TDD）

- RED：翻转 TC-005 后跑过滤测试，`Pointer_activity_far_from_bar_does_not_show_it` 失败且原因正确（旧实现把 `overlay-shown` 加上了）——证明测试抓住了行为差异；其余 6 条旧实现下绿（移近即显本就成立，判别力在"远离不显示"）。
- GREEN：`ScrollBarOverlay` 三处改动——类 doc 显示来源描述、`NearDistance = 24` 常量（含动机注释：命中条仅 10px 且隐藏态不可命中，需在够到细条前可见）、`IsPointerNearBar` 纯几何门控（`TranslatePoint` 求命中条在宿主坐标系的矩形，`Inflate` 外扩后 `Contains` 指针位置；宿主为空/不在同树返回 false）。过滤 7/7 绿。
- 全量 `dotnet test Tokenmetry.sln` = 1446 Passed / 0 Failed（Headless 331→337→345，本卡 +1 净增：拆 1 增 2）。
- 语义保留说明：指针停在邻近区内但不在条上时，闲置到期照样收回（钉住条件仍只有 `IsPointerOver`/拖拽）——"无活动即隐"语义不变，只是"活动"的定义从区内活动收窄为近条活动。
- 有意不做：邻近窗做成可调 attached property（无调参需求，常量 + 测试实坐标锁定契约即可）；指针静止在邻近区内时驻留显示（与"无活动即隐"语义冲突，收益存疑）；Q-004 卡回改（历史记录保持原样，取代关系落在本卡）。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/Tokenmetry.App/Controls/ScrollBarOverlay.cs`、`tests/Tokenmetry.App.Headless.Tests/Controls/ScrollBarOverlayTests.cs`、`docs/design/design-tokens.md`
- How to run/verify: 自动 = `dotnet test tests/Tokenmetry.App.Headless.Tests --filter ScrollBarOverlayTests`；真机 = 卡片「真机人工核验」段
- Results: 完成（2026-10-24）。TC-001~007 全绿；全量 1446 Passed / 0 Failed。
