# Screen Helper — rules for Claude Code

Read docs/PLAN.md before any task. Work one phase at a time, on a branch named phase-N-<topic>.

- Follow the contracts in docs/contracts/ exactly; change a schema only if the task says so.
- ScreenHelper.Core must not reference Windows APIs.
- The app never clicks or types for the user.
- Validate every LLM response against plan.schema.json.
- Never commit secrets. The API key exists only in the Worker environment.
- Run `dotnet test` and `npm test` (in backend/) before every commit.
- When a task is listed under "Tricky parts" in the plan, stop: write the interface,
  leave a `// OPUS: <what is needed>` marker, and continue with the rest.
- A phase is done only when its "Done when" criteria pass; report results in the PR description.
