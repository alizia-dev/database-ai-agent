# AI tools and tool calling

The assistant never writes SQL. It can only call **tools**, and each tool runs exactly one approved stored procedure
or function in the customer database. This page covers how tools are generated, classified, approved and executed
(Phase 6). The agent loop that lets the model call them arrives in Phase 9.

## Generation

After every successful schema analysis, the worker synchronizes tools with the catalog (`IToolCatalogSynchronizer`).
You can also trigger this with `POST /api/database/connections/{id}/tools/generate`.

- Each procedure the connection account can **execute** gets one tool in **Needs review** (`PendingReview`). New
  tools are never enabled automatically.
- **Name**: the routine name in snake_case, without prefixes such as `usp_`/`sp_`/`fn_`. If the name is taken, it
  is qualified with the schema and then numbered. Names are unique across the workspace, at most 64 characters, and
  can be edited.
- **Description**: the catalog comment (or a humanized name), the routine's qualified name, and its result columns.
  This text is what the model sees, and it can be edited.
- **Parameters** become JSON-schema properties with camelCase names (`@CustomerId` → `customerId`,
  `p_min_balance` → `minBalance`):

  | Database types | Tool type | Constraints |
  |---|---|---|
  | `tinyint`/`smallint`/`int`/`bigint` (+ unsigned) | integer | min/max for the declared width |
  | `decimal`/`numeric`/`money`/`float`/`real` | number | |
  | `bit`/`boolean`/MySQL `tinyint(1)` | boolean | |
  | `char`/`varchar`/`text`/`json`/`enum`… | string | `maxLength` = declared length, capped at `Tools:DefaultMaxTextLength` (4,000) |
  | `date` / `datetime*`/`timestamp*` / `time` | string, format `date` / `date-time` / `time` | |
  | `uniqueidentifier`/`uuid` | string, format `uuid` | |

  A parameter is **required** unless the procedure declares a default. Output parameters are not inputs; their
  values are returned. Binary, XML, spatial, array, table-valued and cursor parameters make the tool
  **unsupported**: it is listed with the reason but cannot be approved.
- **Re-analysis**: tool IDs and administrator edits are kept, including parameter descriptions for parameters that
  still exist. If a procedure's definition hash or parameters change, its tool is reclassified. If it was enabled,
  it is **disabled (procedure changed)** until someone approves it again. A procedure that disappears or loses
  EXECUTE disables its tool (**procedure unavailable**). When the procedure comes back, the tool returns to review.
  Rejected tools stay rejected.

## Classification

`ToolClassifier` assigns an operation (READ / WRITE / DELETE / ADMIN) and a risk (Low → Critical). The rules are
conservative: anything uncertain counts as a write.

| Evidence | Operation | Risk |
|---|---|---|
| Definition creates/alters/drops objects or changes permissions | ADMIN | Critical |
| PostgreSQL function declared `STABLE`/`IMMUTABLE` (the engine forbids writes) | READ (by declaration) | Low |
| Definition deletes or truncates | DELETE | Critical |
| Definition inserts/updates/merges, or declares `MODIFIES SQL DATA` | WRITE | High |
| Dynamic SQL or calls to other routines (effects unverifiable) | WRITE | Medium |
| Readable definition with none of the above | READ | Low |
| Definition **not visible** to the account | WRITE (unverified); DELETE if the name suggests deleting | Medium–Critical |

The detail page shows the reasons behind the classification. For an **unverified** routine, an administrator can
confirm that it only reads data (`POST /api/tools/{id}/confirm-read-only`). The tool keeps that classification
only while the definition hash stays the same. A classification derived from the definition cannot be overridden.

## Approval

Users with `tools.manage` approve, reject (with an optional reason), reopen, disable and edit tools. Approval
requires:

- a supported tool whose procedure still exists and is executable;
- operation READ, or `Tools:AllowWriteTools = true` (off by default) together with an explicit acknowledgement
  (`acknowledgeDataChanges: true`) for WRITE/DELETE tools. **ADMIN tools can never be enabled**;
- a plan with production access, and fewer enabled tools than the plan's `MaxApprovedTools`.

Approving records who approved the tool and when, and pins the current definition hash.

**Per-tool settings:** allowed roles (defaults: READ → all roles; WRITE/DELETE → Owner and Admin), a timeout
(1–300 s, default 30), maximum rows (1–10,000, default 500) and maximum result size (1 KB–10 MB, default 1 MB).

## Execution pipeline

`IToolExecutor` is the single entry point for the test-run panel (`POST /api/tools/{name}/execute`) and, from
Phase 9, for the agent. The model's request is untrusted input. Identity and permissions come only from the
authenticated context.

1. **Authentication**: there must be a signed-in user.
2. **Authorization**: the user needs the `tools.execute` permission.
3. **Subscription**: the plan must include production access, and the month's executions must be below
   `MaxToolExecutionsPerMonth`. Otherwise the result is denied with "Monthly tool execution limit reached."
4. **Tool enabled**: the tool must be approved, and its procedure must still exist and be executable.
5. **Tool permission**: the user's current role, read from the database rather than the token, must be in the
   tool's allowed roles.
6. **Validation**: arguments must be a JSON object. Unknown properties are rejected, required ones must be
   present, and types, ranges, lengths and formats are checked. Values are converted to typed parameters and are
   never concatenated into SQL.
7. **Confirmation**: WRITE/DELETE tools return `ConfirmationRequired` with a prompt that names the tool, the
   database and the arguments. They run only when the same request is resent with `confirmed: true`, and this
   applies to every execution.
8. **Timeout and execution**: the procedure runs with the tool's timeout and limits (see below).
9. **Audit**: each attempt that names an existing tool is stored in `tool_executions`, including denials and
   confirmation requests. A record holds the user, status, reason code, duration, row count, result size, whether
   the result was truncated and whether the user confirmed. Argument values are redacted (text values are reduced
   to their length), and result rows are never stored.

Outcomes: `Succeeded`, `ConfirmationRequired`, `Denied`, `InvalidArguments` (with per-argument `argumentErrors`),
`Failed`, `TimedOut`. Each carries a stable `reasonCode` (`tools.permission_denied`, `tools.disabled`,
`tools.role_not_allowed`, `billing.limit_reached`, `tools.timeout`, `tools.database_unavailable`,
`tools.read_only_violation`, `tools.busy`, `tools.database_error`, …) and a message that is safe to display.
Database error text is never returned.

## Invoking procedures safely

`CustomerProcedureInvoker` (Database module):

- **Commands**: only catalog-derived identifiers are used, quoted per engine (`[x]`, `"x"`, `` `x` ``). Every value
  is a typed parameter.
  - SQL Server and MySQL procedures use `CommandType.StoredProcedure`.
  - PostgreSQL uses `CALL schema.proc(name => @p0)`, `SELECT * FROM schema.func(name => @p0)` or `SELECT schema.func(...)`.
  - Omitted optional arguments use the routine's defaults.
  - Integers are sent at the declared width, because PostgreSQL resolves functions by argument type.
- **Read-only enforcement**: READ tools run inside a transaction.
  - PostgreSQL uses `SET TRANSACTION READ ONLY`; MySQL uses `START TRANSACTION READ ONLY`. Both reject writes.
  - SQL Server always rolls the transaction back.
  - A misclassified procedure therefore cannot persist changes. Attempts are reported as
    `tools.read_only_violation` and logged.
- **Limits**: reading stops after `MaxRows` rows or `MaxResultBytes` bytes, and the result is marked truncated.
  Binary values are summarized, and long text is capped at 2,000 characters.
- **Resilience** (`ToolExecution` section):
  - Each customer database accepts at most `MaxConcurrentPerConnection` (5) concurrent calls; further calls fail
    fast as busy.
  - After `CircuitBreakerFailures` (5) consecutive connection failures or timeouts, calls to that database are
    paused for `CircuitBreakerSeconds` (30).
  - The host is re-checked against the SSRF guard on every open.
- **Rate limit**: `ApiSecurity:ToolExecutionRateLimitPermitsPerMinute` (30) per user.

## Configuration

```json
"Tools": { "AllowWriteTools": false, "DefaultTimeoutSeconds": 30, "DefaultMaxRows": 500, "DefaultMaxTextLength": 4000 },
"ToolExecution": { "MaxConcurrentPerConnection": 5, "CircuitBreakerFailures": 5, "CircuitBreakerSeconds": 30 }
```

## UI

**AI tools** (`/app/tools`, `tools.view`) lists tools with their status, operation and risk, filtered by database,
status and search text. A tool's detail page shows:

- its classification reasons and review actions;
- settings: name, descriptions, limits and roles;
- a **Test run** panel that runs the tool through the same pipeline, including confirmation for writes;
- recent runs.
