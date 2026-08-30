# Style

How code in this repository is written. `CLAUDE.md` holds domain rules that are *bugs* when
broken; this file holds conventions that are *inconsistency* when broken. Both matter, but
they fail differently.

Most of these exist because they're the points where two developers — or two sessions of the
same agent — reliably diverge.

## Formatting is not a decision

csharpier owns layout. `.editorconfig` owns naming and analyzer severity. Neither is worth
arguing about, and neither should be duplicated here.

```bash
dotnet csharpier format .     # before committing
dotnet csharpier check .      # what CI runs
```

Print width is **120**, matching `max_line_length` in `.editorconfig`. Line endings are LF
everywhere — set in `.csharpierrc.json`, `.editorconfig` and `.gitattributes`.

## Domain modelling

**IDs are strongly typed.** This domain is full of `long`s — team, chat, user, message, game,
signup, franchise — and `Promote(gameId, userId)` compiles just as happily with the arguments
swapped.

```csharp
public readonly record struct GameId(long Value);
```

EF Core maps them with value converters. The ceremony is small; the class of bug it removes is
silent and this codebase is unusually exposed to it, because code written without the domain
in your head gets argument order wrong more often.

**Entities are classes. Value objects are records.** EF tracks classes; records give value
equality where that's what you want (a franchise schedule, a placement).

**Roster logic is pure static functions in `Quizr.Domain`, not methods on entities.** Invariant
2 says the playing/reserve split is derived from an ordered list. Derived state doesn't belong
on a tracked entity.

## Errors

Three mechanisms, no overlap between them.

| Kind | Mechanism |
| --- | --- |
| Business failure — "you aren't a captain", "registration is closed" | `Result<T>` carrying a `BusinessError` |
| Infrastructure fault — database unreachable, Telegram API down | Exception, caught at exactly two boundaries |
| Programmer error | Exception, never caught |

### Business failures

A **closed hierarchy** of error types in `Quizr.Domain`, not per-operation enums:

```csharp
public abstract record BusinessError
{
    public sealed record NotCaptain          : BusinessError;
    public sealed record RegistrationClosed  : BusinessError;
    public sealed record AlreadySignedUp     : BusinessError;
    public sealed record GameAlreadyFinished : BusinessError;
}
```

The reason it's shared rather than per-operation: **"not a captain" is the same failure across
create, edit, finish, decline, mark-attendance and remove-player.** Per-operation enums would
repeat it a dozen times, with a dozen mappings to a message key and a dozen chances to
translate it differently. Shared, there is **one** error-type-to-message-key mapping, and it's
translated once into three languages.

**Success payloads are not errors.** Landing in the reserve at position 19 is a successful
signup carrying information:

```csharp
Result<SignupPlacement> SignUp(...)   // success: playing-or-reserve, plus position
                                      // failure: AlreadySignedUp | RegistrationClosed
```

**Authorization is checked in the application service, not the dispatcher** — so it returns
`NotCaptain` rather than being enforced before the call. In phase 2 the bot handler and the
HTTP endpoint call the same method and get the same answer, instead of each remembering to
check first.

### Infrastructure faults

Exceptions, because the handling strategy is uniform and centralised, and because EF Core,
Npgsql and Telegram.Bot all throw — a `Result`-based error model would mean wrapping every
third-party call to convert exceptions into results, which is *more* exception handling, not
less.

Exactly two places catch broadly:

- **The update dispatch boundary** — one failing handler must not take the bot down. Log (the
  update scope is already attached), reply with a generic apology in the right language, alert
  the private channel.
- **The scheduler tick** — one broken game must not stop reminders for everyone else.

**A broad `catch` anywhere else is almost always someone hiding a fault.** Catch narrowly, to
add context, and rethrow.

## Types and members

**No primary constructors.** Ever. Their captured parameters cannot be `readonly`, which
defeats the rule below.

**`readonly` on everything that doesn't change** — injected dependencies especially.
`dotnet_style_readonly_field` is a warning and warnings are errors, so the compiler finds these
rather than a reviewer.

**`sealed` by default.** Anything not deliberately designed for inheritance.

**Interfaces where they earn it:**

- **Yes** for boundaries faked in tests — the Telegram sender, `IStringsFor`, the clock — and
  for application services, whose contract is worth reading and which both front doors will
  share in phase 2.
- **No** for internal helpers, renderers or data.
- **Same file, named after the implementation** — `GameService.cs` holds `IGameService` and
  `GameService`. One file to edit, no navigation detour.

Note the interaction: **`sealed` plus no interface means NSubstitute cannot fake the type at
all.** Anything faked in a test needs an interface.

Collection expressions (`[]`) and target-typed `new` where the type is already on the line:
yes, both.

## Async

- **Thread `CancellationToken` everywhere**, from the hosted service down. Graceful shutdown
  depends on it, and a token that stops halfway is worse than none.
- `Async` suffix on async methods. No `async void`.
- **No `ConfigureAwait(false)`.** There is no synchronization context in a generic host. It is
  noise, and it's the single most common reflexive addition to this kind of code.

## Dependency injection

- **Constructor injection only.** No service locator, no injected `IServiceProvider` outside
  the composition root.
- **One DI scope per Telegram update.** `DbContext` is scoped and the dispatcher opens the
  scope. Getting this wrong produces a shared `DbContext` across concurrent updates, which
  fails in ways that look random.

## EF Core

- **No lazy loading, ever.** Explicit `Include`.
- **`AsNoTracking()`** on reads that don't write — which is most of them, since rendering a
  message is a read.
- **Queries live in `Quizr.App`.** `Quizr.Domain` has no EF reference and never will.
- Migrations get descriptive names and are read before they're applied.
- **`SingleAsync`/`SingleOrDefaultAsync` over `FirstAsync`/`FirstOrDefaultAsync`** wherever the
  predicate matches a primary key, a unique index, or an invariant the application layer
  already enforces (e.g. at most one live signup per game and player). `First` silently
  accepts a second match; `Single` throws, which is what turns a broken uniqueness assumption
  into a loud bug instead of a query that quietly picks one row and moves on. `First`/
  `FirstOrDefault` stay fine for in-memory LINQ once singularity is already guaranteed by
  prior logic — there the difference is real (`Single` has to scan the whole sequence to
  confirm no second match; `First` short-circuits), so it's a legitimate performance choice,
  not a correctness one.

## Comments

**Line comments (`//`) only.** No `/* */`.

**Comment the why, never the what.** If a comment restates the code, delete it — this is the
most reliable difference between human and generated code in this repository. Prefer making
the code say it: a well-named method needs no header.

The comment in `Quizr.Domain.csproj` is the model — it explains why the project references
nothing, which the file cannot say for itself.

**No XML documentation comments** except on genuinely non-obvious public surface. This is an
application, not a library.

## Testing

- **Sentence-style names**: `PromotesFirstReserveWhenSomeoneDrops`. Reads properly in output,
  unlike `Promote_Drop_Success`.
- Arrange, act, assert — without the comment labels.
- **Test data builders** for games and rosters. You will construct twenty-signup scenarios
  constantly, and inline setup will bury the point of each test.
- **Fake only at boundaries.** Prefer real objects everywhere else. `Quizr.Domain.Tests` has no
  mocking library at all, on purpose — if a mock seems necessary there, something has leaked
  into the domain.
- Every plural template gets a snapshot test at 1, 2, 5, 21 and 111. See `CLAUDE.md`.

## For agents specifically

- **Check `STACK.md`'s anti-list before adding any package.** Several obvious choices were
  rejected deliberately and the reasons are written down.
- **Don't introduce an abstraction with one implementation** unless it's a boundary or a
  service contract per the rule above.
- **No defensive null checks** where nullable reference types already guarantee non-null.
- **Match the surrounding code** over your own preference.
- **Delete code rather than commenting it out.** Git remembers.
- **Don't restate the task in comments.** See above; it's the most common tell.
