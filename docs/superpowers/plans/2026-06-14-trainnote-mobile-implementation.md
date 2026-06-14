# Trainnote Mobile Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a cross-platform Expo React Native MVP for a 训记-like training log app with Supabase auth, cloud data schema, training plans, workout set entry, history, and stats.

**Architecture:** Create an isolated `trainnote-mobile/` Expo Router app. Keep domain logic in small tested TypeScript modules under `src/features/*`, keep Supabase access behind feature API files, and keep screens focused on rendering, input, navigation, and loading/error state.

**Tech Stack:** Expo React Native, TypeScript, Expo Router, Supabase JS, AsyncStorage, React Native Testing Library, Jest, Postgres SQL schema for Supabase.

---

## File Structure

- Create `trainnote-mobile/` with Expo Router TypeScript template.
- Create `trainnote-mobile/src/theme/tokens.ts` for color, spacing, radius, and typography constants.
- Create `trainnote-mobile/src/models/training.ts` for shared domain types.
- Create `trainnote-mobile/src/features/workouts/validation.ts` and `validation.test.ts` for set input validation.
- Create `trainnote-mobile/src/features/stats/stats.ts` and `stats.test.ts` for volume, weekly aggregation, and PR calculations.
- Create `trainnote-mobile/src/lib/env.ts` and `supabase.ts` for environment parsing and Supabase client initialization.
- Create `trainnote-mobile/src/features/auth/api.ts`, `exercises/api.ts`, `plans/api.ts`, `workouts/api.ts` for Supabase operations.
- Create `trainnote-mobile/src/components/` for reusable UI primitives.
- Create Expo Router screens under `trainnote-mobile/app/`.
- Create `trainnote-mobile/supabase/schema.sql` and `seed.sql`.
- Create `trainnote-mobile/.env.example`.
- Modify `trainnote-mobile/package.json` scripts for test and typecheck.

## Task 1: Scaffold Expo Project

**Files:**
- Create: `trainnote-mobile/`
- Modify: `trainnote-mobile/package.json`

- [ ] **Step 1: Generate Expo app**

Run:

```powershell
npx create-expo-app@latest trainnote-mobile --template default
```

Expected: `trainnote-mobile/package.json`, `app/`, `assets/`, and TypeScript config exist.

- [ ] **Step 2: Install app dependencies**

Run:

```powershell
cd trainnote-mobile
npm install @supabase/supabase-js @react-native-async-storage/async-storage react-native-url-polyfill
npm install --save-dev jest-expo @testing-library/react-native @testing-library/jest-native @types/jest
```

Expected: dependencies are added to `package.json`.

- [ ] **Step 3: Add scripts**

Update `trainnote-mobile/package.json` scripts:

```json
{
  "test": "jest --watchAll=false",
  "typecheck": "tsc --noEmit"
}
```

- [ ] **Step 4: Run baseline checks**

Run:

```powershell
npm run typecheck
npm test -- --runInBand
```

Expected: baseline typecheck passes or reveals only template-related issues to address before feature work.

## Task 2: Add Domain Types and Validation With TDD

**Files:**
- Create: `trainnote-mobile/src/models/training.ts`
- Create: `trainnote-mobile/src/features/workouts/validation.test.ts`
- Create: `trainnote-mobile/src/features/workouts/validation.ts`

- [ ] **Step 1: Write failing validation tests**

Create tests for valid set input and invalid weight/reps/RPE/rest values:

```ts
import { validateWorkoutSetInput } from "./validation";

test("accepts a valid workout set input", () => {
  expect(validateWorkoutSetInput({ weight: "80.5", reps: "8", rpe: "8.5", restSeconds: "120" })).toEqual({
    ok: true,
    value: { weight: 80.5, reps: 8, rpe: 8.5, restSeconds: 120 },
  });
});

test("rejects invalid numeric workout set input", () => {
  const result = validateWorkoutSetInput({ weight: "-1", reps: "0", rpe: "11", restSeconds: "-30" });
  expect(result.ok).toBe(false);
  if (!result.ok) {
    expect(result.errors).toEqual(expect.arrayContaining(["重量不能为负数", "次数必须大于 0", "RPE 必须在 0 到 10 之间", "休息时间不能为负数"]));
  }
});
```

- [ ] **Step 2: Run test to verify RED**

Run:

```powershell
npm test -- src/features/workouts/validation.test.ts --runInBand
```

Expected: FAIL because `validation.ts` does not exist.

- [ ] **Step 3: Implement validation minimally**

Create `validateWorkoutSetInput` returning a discriminated union:

```ts
export type WorkoutSetInput = {
  weight: string;
  reps: string;
  rpe: string;
  restSeconds: string;
};

export type ValidationResult =
  | { ok: true; value: { weight: number; reps: number; rpe: number; restSeconds: number } }
  | { ok: false; errors: string[] };
```

- [ ] **Step 4: Run test to verify GREEN**

Run:

```powershell
npm test -- src/features/workouts/validation.test.ts --runInBand
```

Expected: PASS.

## Task 3: Add Stats Pure Functions With TDD

**Files:**
- Create: `trainnote-mobile/src/features/stats/stats.test.ts`
- Create: `trainnote-mobile/src/features/stats/stats.ts`

- [ ] **Step 1: Write failing stats tests**

Test total volume, weekly session count, and PR by exercise:

```ts
import { calculateTotalVolume, calculateExercisePrs, countSessionsByWeek } from "./stats";

const sets = [
  { exerciseId: "bench", weight: 80, reps: 8, createdAt: "2026-06-08T10:00:00Z" },
  { exerciseId: "bench", weight: 90, reps: 5, createdAt: "2026-06-10T10:00:00Z" },
  { exerciseId: "squat", weight: 100, reps: 6, createdAt: "2026-06-11T10:00:00Z" },
];

test("calculates total training volume", () => {
  expect(calculateTotalVolume(sets)).toBe(1690);
});

test("calculates best weight PR per exercise", () => {
  expect(calculateExercisePrs(sets)).toEqual({ bench: 90, squat: 100 });
});

test("counts completed sessions by ISO week key", () => {
  expect(countSessionsByWeek([{ id: "s1", finishedAt: "2026-06-10T10:00:00Z" }])).toEqual({ "2026-W24": 1 });
});
```

- [ ] **Step 2: Run test to verify RED**

Run:

```powershell
npm test -- src/features/stats/stats.test.ts --runInBand
```

Expected: FAIL because functions do not exist.

- [ ] **Step 3: Implement pure stats functions**

Implement functions without Supabase or UI dependencies.

- [ ] **Step 4: Run test to verify GREEN**

Run:

```powershell
npm test -- src/features/stats/stats.test.ts --runInBand
```

Expected: PASS.

## Task 4: Add Supabase Schema and Seed Data

**Files:**
- Create: `trainnote-mobile/supabase/schema.sql`
- Create: `trainnote-mobile/supabase/seed.sql`
- Create: `trainnote-mobile/__tests__/schema.test.ts`

- [ ] **Step 1: Write failing schema test**

Create a test that reads `schema.sql` and asserts required tables and RLS policies exist:

```ts
import fs from "node:fs";
import path from "node:path";

test("schema defines required training tables and RLS", () => {
  const sql = fs.readFileSync(path.join(__dirname, "../supabase/schema.sql"), "utf8");
  for (const table of ["profiles", "exercises", "training_plans", "plan_exercises", "workout_sessions", "workout_sets"]) {
    expect(sql).toContain(`create table if not exists public.${table}`);
    expect(sql).toContain(`alter table public.${table} enable row level security`);
  }
  expect(sql).toContain("auth.uid()");
});
```

- [ ] **Step 2: Run test to verify RED**

Run:

```powershell
npm test -- __tests__/schema.test.ts --runInBand
```

Expected: FAIL because schema file does not exist.

- [ ] **Step 3: Write SQL schema**

Create tables from the approved spec, add indexes, `updated_at` trigger function, and RLS policies for owner-scoped rows. Built-in exercises should be readable by all authenticated users.

- [ ] **Step 4: Add seed data**

Add common exercises to `seed.sql`: 卧推, 深蹲, 硬拉, 引体向上, 坐姿划船, 肩推, 弯举, 下压.

- [ ] **Step 5: Run schema test to verify GREEN**

Run:

```powershell
npm test -- __tests__/schema.test.ts --runInBand
```

Expected: PASS.

## Task 5: Add Supabase Client and Feature APIs

**Files:**
- Create: `trainnote-mobile/.env.example`
- Create: `trainnote-mobile/src/lib/env.ts`
- Create: `trainnote-mobile/src/lib/supabase.ts`
- Create: `trainnote-mobile/src/features/auth/api.ts`
- Create: `trainnote-mobile/src/features/exercises/api.ts`
- Create: `trainnote-mobile/src/features/plans/api.ts`
- Create: `trainnote-mobile/src/features/workouts/api.ts`

- [ ] **Step 1: Write env and API smoke tests**

Test env parsing does not expose secrets beyond public Expo values and API modules export expected functions.

- [ ] **Step 2: Run tests to verify RED**

Run:

```powershell
npm test -- src/lib/env.test.ts --runInBand
```

Expected: FAIL because env module does not exist.

- [ ] **Step 3: Implement env and Supabase client**

Initialize Supabase with:

```ts
import AsyncStorage from "@react-native-async-storage/async-storage";
import { createClient } from "@supabase/supabase-js";
import "react-native-url-polyfill/auto";
```

- [ ] **Step 4: Implement feature API functions**

Add minimal functions:

- `signInWithEmail`
- `signUpWithEmail`
- `signOut`
- `listExercises`
- `createCustomExercise`
- `listPlans`
- `createPlan`
- `addExerciseToPlan`
- `startWorkoutSession`
- `saveWorkoutSet`
- `completeWorkoutSession`
- `listRecentSessions`

- [ ] **Step 5: Run typecheck**

Run:

```powershell
npm run typecheck
```

Expected: PASS.

## Task 6: Build Navigation and Screens

**Files:**
- Modify: `trainnote-mobile/app/_layout.tsx`
- Create/modify: `trainnote-mobile/app/(auth)/index.tsx`
- Create/modify: `trainnote-mobile/app/(tabs)/_layout.tsx`
- Create: `trainnote-mobile/app/(tabs)/index.tsx`
- Create: `trainnote-mobile/app/(tabs)/plans.tsx`
- Create: `trainnote-mobile/app/(tabs)/workout.tsx`
- Create: `trainnote-mobile/app/(tabs)/history.tsx`
- Create: `trainnote-mobile/app/(tabs)/stats.tsx`
- Create: `trainnote-mobile/app/(tabs)/profile.tsx`
- Create: `trainnote-mobile/src/components/AppButton.tsx`
- Create: `trainnote-mobile/src/components/AppTextField.tsx`
- Create: `trainnote-mobile/src/components/MetricCard.tsx`
- Create: `trainnote-mobile/src/theme/tokens.ts`

- [ ] **Step 1: Write UI smoke tests**

Use React Native Testing Library to assert login fields, plan empty state, and workout set inputs render.

- [ ] **Step 2: Run UI tests to verify RED**

Run:

```powershell
npm test -- __tests__/ui-smoke.test.tsx --runInBand
```

Expected: FAIL because screens/components do not exist.

- [ ] **Step 3: Implement theme and components**

Use restrained, training-tool UI with soft accent color, stable dimensions, and compact controls. Avoid marketing hero layouts.

- [ ] **Step 4: Implement auth and tab layouts**

Auth screen supports sign in/sign up mode. Tabs are 今日, 计划, 开练, 历史, 统计, 我的.

- [ ] **Step 5: Implement MVP screen behavior**

Use feature APIs and local component state. Show loading, empty, and error states for each screen.

- [ ] **Step 6: Run UI tests to verify GREEN**

Run:

```powershell
npm test -- __tests__/ui-smoke.test.tsx --runInBand
```

Expected: PASS.

## Task 7: Verify App Locally

**Files:**
- Modify only if verification finds defects.

- [ ] **Step 1: Run full tests**

Run:

```powershell
npm test -- --runInBand
```

Expected: PASS.

- [ ] **Step 2: Run typecheck**

Run:

```powershell
npm run typecheck
```

Expected: PASS.

- [ ] **Step 3: Start Expo**

Run:

```powershell
npx expo start --web
```

Expected: Expo starts and serves the app. If Supabase env vars are missing, app should still show a clear configuration message instead of crashing.

- [ ] **Step 4: Browser verify web preview**

Open the Expo web URL and verify:

- Login/register screen renders.
- Tabs render after mock/demo fallback or after real Supabase credentials.
- Text fits in mobile viewport.
- No incoherent overlaps.

## Task 8: Commit Implementation

**Files:**
- Stage only `trainnote-mobile/` and plan/spec changes relevant to this app.

- [ ] **Step 1: Check git status**

Run:

```powershell
git status --short
```

Expected: Existing unrelated untracked files remain untouched. New app files are visible under `trainnote-mobile/`.

- [ ] **Step 2: Stage relevant files**

Run:

```powershell
git add -- trainnote-mobile docs/superpowers/plans/2026-06-14-trainnote-mobile-implementation.md
```

- [ ] **Step 3: Commit**

Run:

```powershell
git commit -m "Build trainnote mobile MVP"
```

Expected: commit succeeds.
