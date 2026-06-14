# 训记类跨端训练记录 App 设计

日期：2026-06-14

## 背景

目标是做一个类似“训记”的跨端移动 App 原型，覆盖 iOS 和 Android。用户已选择真实手机 App 原型，并选择账号和云同步。第一版应优先把力量训练记录体验做完整：能登录、建计划、选择动作、开始训练、逐组记录重量/次数/RPE/休息、查看历史和基础统计。

## 推荐技术路线

采用 Expo React Native + TypeScript 构建移动端，使用 Supabase 提供邮箱密码认证、Postgres 数据库存储、Row Level Security 用户隔离和云端同步。

选择理由：

- Expo 可以用一个 React Native 工程覆盖 iOS、Android 和 Web 预览，初期可通过 Expo Go 快速调试。
- Supabase 直接提供认证、数据库和权限能力，避免第一版自建认证后端。
- 训练记录是结构化数据，Postgres 比文档数据库更适合做容量、次数、PR、按周/月统计等查询。

参考：

- Expo create project 文档：https://docs.expo.dev/get-started/create-a-project/
- Supabase Expo React Native quickstart：https://supabase.com/docs/guides/getting-started/quickstarts/expo-react-native
- Supabase Auth React Native quickstart：https://supabase.com/docs/guides/auth/quickstarts/react-native

## 第一版范围

包含：

- 邮箱 + 密码注册、登录、退出登录。
- 首页展示今天训练入口、最近训练、周训练次数、总训练容量。
- 动作库展示内置常见力量训练动作，支持新增自定义动作。
- 训练计划支持创建、命名、添加动作、排序。
- 开练流程支持从计划开始训练，逐组记录重量、次数、RPE、休息秒数、备注。
- 历史页按日期查看训练 session 和每个动作的组记录。
- 统计页展示周/月训练次数、总容量、主要动作 PR。
- 我的页展示账号、同步状态、退出登录。
- 所有用户数据写入 Supabase，并通过 RLS 限制只能访问自己的数据。

不包含：

- 支付、会员体系、社交动态、教练端、学员管理。
- Apple Watch、健康 App 写入、推送通知。
- AI 生成训练计划。
- 饮食记录。
- 复杂离线冲突合并。
- 上架 App Store / Google Play。

## 用户体验结构

使用底部 Tab：

- 今日：训练入口和近期概览。
- 计划：训练计划列表和计划编辑。
- 开练：当前训练记录器。
- 历史：日历/列表查看历史训练。
- 统计：训练容量、次数和 PR。
- 我的：账号和同步状态。

开练页是核心界面：

1. 选择一个训练计划。
2. App 创建 `workout_session`。
3. 展示计划中的动作列表。
4. 用户逐组输入重量、次数、RPE、休息时间和备注。
5. 每组保存为 `workout_sets`。
6. 结束训练后 session 标记完成，并更新统计视图。

界面风格应偏训练工具：清爽、稳定、可快速输入。允许轻微可爱感，例如柔和配色、圆润图标和友好空状态，但不做花哨营销页。

## 数据模型

Supabase 表：

### profiles

- `id uuid primary key references auth.users(id)`
- `display_name text`
- `created_at timestamptz`
- `updated_at timestamptz`

### exercises

- `id uuid primary key`
- `owner_id uuid nullable references auth.users(id)`
- `name text not null`
- `primary_muscle text`
- `equipment text`
- `is_builtin boolean not null default false`
- `created_at timestamptz`
- `updated_at timestamptz`

规则：内置动作 `owner_id` 为空且所有用户可读；自定义动作 `owner_id` 为当前用户且仅自己可读写。

### training_plans

- `id uuid primary key`
- `owner_id uuid not null references auth.users(id)`
- `name text not null`
- `note text`
- `created_at timestamptz`
- `updated_at timestamptz`

### plan_exercises

- `id uuid primary key`
- `plan_id uuid not null references training_plans(id)`
- `exercise_id uuid not null references exercises(id)`
- `position integer not null`
- `target_sets integer`
- `target_reps text`
- `rest_seconds integer`
- `note text`

### workout_sessions

- `id uuid primary key`
- `owner_id uuid not null references auth.users(id)`
- `plan_id uuid references training_plans(id)`
- `started_at timestamptz not null`
- `finished_at timestamptz`
- `status text not null`
- `note text`

`status` 初始为 `active`，结束后为 `completed`。

### workout_sets

- `id uuid primary key`
- `session_id uuid not null references workout_sessions(id)`
- `exercise_id uuid not null references exercises(id)`
- `set_index integer not null`
- `weight numeric`
- `reps integer`
- `rpe numeric`
- `rest_seconds integer`
- `note text`
- `created_at timestamptz`

训练容量按 `weight * reps` 计算。第一版不把派生统计写回主表，统计页运行查询后在客户端聚合。

## 同步和权限

- 客户端通过 Supabase JS SDK 访问 Auth 和数据库。
- 登录状态存储在安全本地存储中。
- 训练数据写入远端 Supabase。
- Row Level Security 以 `auth.uid()` 校验 `owner_id`。
- 第一版不实现离线编辑队列。网络失败时显示失败状态，并允许用户重试当前保存动作。
- 当前训练页保存每一组后刷新本地状态；保存失败时该组保留在界面中并标记未同步。

## 错误处理

- 登录失败：展示明确错误，不清空输入。
- 网络失败：展示同步失败提示，并保留用户输入。
- 空计划：引导创建计划或直接从动作库添加动作。
- 空动作库：仍允许创建自定义动作。
- 数值输入：重量允许小数，次数和休息时间必须为非负整数，RPE 范围 0-10。
- 结束训练前如存在未保存组，提示用户先重试或放弃这些组。

## 代码结构

目标子项目：`trainnote-mobile/`

建议结构：

```text
trainnote-mobile/
  app/
    (auth)/
    (tabs)/
    workout/
  src/
    components/
    features/
      auth/
      exercises/
      plans/
      workouts/
      stats/
    lib/
      supabase.ts
      env.ts
    models/
    theme/
  supabase/
    schema.sql
    seed.sql
  __tests__/
```

关键边界：

- `src/lib/supabase.ts` 只负责初始化客户端。
- `src/features/*/api.ts` 封装 Supabase 查询和写入。
- `src/features/*/types.ts` 定义领域类型。
- 页面组件只处理输入、导航和状态展示。
- 统计计算函数保持纯函数，方便测试。

## 测试策略

第一版测试重点：

- 统计纯函数：总容量、按周聚合、PR 计算。
- 输入验证：重量、次数、RPE、休息时间。
- 数据映射：Supabase 行数据到 App 类型。
- 关键 UI smoke test：登录表单、计划列表空状态、开练记录器渲染。
- Supabase schema 静态检查：RLS policy 和必要表存在。

实现阶段应优先对纯逻辑写测试，再写功能代码。涉及 Supabase 的远端集成测试可先用 schema SQL 和 mock client 覆盖，真实项目 URL 和 anon key 通过环境变量配置。

## 环境变量

需要：

- `EXPO_PUBLIC_SUPABASE_URL`
- `EXPO_PUBLIC_SUPABASE_ANON_KEY`

这些值不提交到仓库。提供 `.env.example`。

## 验收标准

- 能在本地启动 Expo 项目。
- 能看到登录/注册界面。
- 配置 Supabase 环境变量后能注册、登录、退出。
- 登录后能创建训练计划。
- 能从计划开始训练。
- 能保存至少一个动作的多组重量、次数、RPE 和休息时间。
- 历史页能看到刚保存的训练。
- 统计页能计算并展示训练容量。
- 用户 A 不能读取用户 B 的训练数据。
- 测试命令通过，且无明显 TypeScript 错误。

## 后续扩展

- 离线队列和冲突合并。
- 动作图示和动作教学。
- 模板分享。
- 教练/学员模式。
- 饮食记录。
- Apple Health / Google Fit 集成。
- EAS development build 和正式打包。
