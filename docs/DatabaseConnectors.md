# Database connectors

The platform connects to the customer's **existing** SQL Server, PostgreSQL or MySQL database. Nothing is migrated
or copied: only metadata (and, in later phases, the results of approved stored procedures) passes through the
platform. Connections are managed under **Databases** (`/app/database`) by users with `database.manage`, on a plan
that includes production access. The number of connections is limited by the plan (`MaxDatabaseConnections`).

## Supported engines

| Engine | Driver | Default port | Notes |
|---|---|---|---|
| SQL Server (2016+, Azure SQL) | Microsoft.Data.SqlClient 7.1 | 1433 | SQL authentication. Named instances (`server\instance`) are not supported: use the instance's TCP port. |
| PostgreSQL (12+) | Npgsql 10 | 5432 | Single host only. |
| MySQL (8.0+) / MariaDB | MySqlConnector 2.6 | 3306 | `LOAD DATA LOCAL INFILE`, user variables and public-key retrieval are always disabled. |

Every engine sits behind `ICustomerDatabaseProvider`. Adding another engine means implementing that interface (build
a connection, test it, parse pasted connection strings, provide a setup script) and registering it in `DatabaseModule`.

## Entering a connection

Enter host, port, database, username, password and TLS mode, or paste a connection string. A pasted string is parsed
into the same fields with the driver's own parser. Only host, port, database, user, password and TLS settings are
kept; **every other option is dropped**, reported back as "ignored", and never stored. Connection strings are always
rebuilt from the fields with the driver's builder plus a fixed set of safe options, so a value such as a password
containing `;Integrated Security=true` cannot change the connection's behaviour.

**Every save runs a live test first.** A failed test blocks saving and returns a safe category (unreachable,
authentication failed, database not found, timeout, TLS failure, permission denied), never the driver's raw message.
On update, leave the password empty to keep the stored credentials.

## TLS modes

| Mode | SQL Server | PostgreSQL | MySQL |
|---|---|---|---|
| Verify full (default, recommended) | `Encrypt=Mandatory`, certificate validated | `SslMode=VerifyFull` | `SslMode=VerifyFull` |
| Required | `Encrypt=Mandatory; TrustServerCertificate=true` | `Require` | `Required` |
| Preferred | `Encrypt=Optional` | `Prefer` | `Preferred` |
| Disabled | `Encrypt=Optional` | `Disable` | `None` |

Anything weaker than *Verify full* shows a warning on the connection.

## Least-privilege account (recommended)

Use a dedicated account that can only **EXECUTE** the stored procedures you approve, with no direct table access. The
connect page shows a ready-to-run script for the selected engine; the same scripts are returned by
`GET /api/database/providers`. In summary:

- **SQL Server:** create a login and database user, `GRANT VIEW DEFINITION` (to read procedure metadata), and
  `GRANT EXECUTE` on the specific procedures or on a dedicated `reporting` schema. Never add the user to
  `db_owner`, `db_datareader` or `db_datawriter`.
- **PostgreSQL:** create a `LOGIN NOSUPERUSER` role with `CONNECT` and `USAGE` on the reporting schema, and revoke the
  default `EXECUTE ... FROM PUBLIC`. Then `GRANT EXECUTE` on the specific functions/procedures, written as
  `SECURITY DEFINER` so the role needs no table privileges.
- **MySQL:** create a user with only `GRANT EXECUTE ON PROCEDURE db.proc`, with procedures defined as
  `SQL SECURITY DEFINER`. Restrict the host part to the platform's egress IP address where possible.

Each test inspects the account's privileges and warns when it is an administrator, can read tables directly, can
write data, or cannot execute any procedure. Warnings do not block saving; the recommendation is up to the customer.

## Network access and SSRF protection

The host must be a plain DNS name or IP literal: no protocol, path, port, instance or connection-string syntax.
Before every connection (on save, on test and on every later open) the host is resolved, and **every** resolved address
is checked:

| Range | Default | Setting |
|---|---|---|
| Link-local incl. cloud metadata (`169.254.0.0/16`, `fe80::/10`, `fd00:ec2::254`, `100.100.100.200`), `0.0.0.0/8`, multicast, broadcast, reserved | always blocked | none |
| Loopback (`127.0.0.0/8`, `::1`) | blocked | `DatabaseConnections:AllowLoopback` (Development only) |
| Private (`10/8`, `172.16/12`, `192.168/16`, `100.64/10`, `fc00::/7`) | blocked | `DatabaseConnections:AllowPrivateNetworks` |

Internet-facing SaaS deployments keep private networks blocked, so users cannot probe the platform's own network.
Deployments inside the customer's network (VPC, on-premises) set `AllowPrivateNetworks=true`. Connection tests are
rate limited per IP (`ApiSecurity:ConnectionTestRateLimitPermitsPerMinute`, default 10). Ask customers to allow the
deployment's outbound IP address in their database firewall.

## Credential encryption

- The username and password are encrypted together with **AES-256-GCM** (`ISecretProtector`). The result is bound
  to the connection's ID through associated data, so it cannot be copied to another record. Host, port and database
  name are stored in clear text for display and network checks.
- The key comes from `ENCRYPTION_KEY` (`Encryption__Key`): 32 random bytes, base64
  (`openssl rand -base64 32`), supplied by the secret manager (Azure Key Vault / AWS Secrets Manager). Startup fails
  outside Development if it is missing or malformed. Development uses a per-machine key file in the user profile
  (`%LOCALAPPDATA%/DatabaseAIAgent/development-encryption.key`), never in the repository.
- **Rotation:** move the current key to `Encryption__PreviousKeys__0` and set a new `Encryption__Key`. Existing
  values stay readable, and new and re-saved values use the new key (ciphertexts carry a key fingerprint).
  Re-save connections, then remove the old key.
- Credentials are never returned by the API (not even the username), never logged, and `ResolvedConnection` (the
  decrypted form) formats without them.
- Deleting a connection removes only the platform's metadata and encrypted credentials. **The customer's database is
  never modified or deleted.**

## Private-network connector (design, not yet implemented)

For databases that must not be reachable from the internet, a small **connector agent** will run inside the
customer's network:

1. It is installed as a container or Windows service with a one-time registration token, and opens an **outbound-only**
   TLS connection (WebSocket/gRPC) to the deployment, so no inbound firewall rule is needed.
2. Connections are marked "via connector". The platform sends only approved, parameterized procedure calls over the
   tunnel. The agent holds the database credentials locally and enforces the same timeouts and row limits.
3. Mutual TLS with per-connector certificates, an allowlist of procedure names on the agent side, and audit logging on
   both ends.

`ICustomerDatabaseConnector` is the seam: callers open connections by ID and do not need to know whether the transport
is direct or tunnelled.

## Testing

- Unit tests cover the host guard, encryption (tampering, wrong owner, rotation), connection-string building
  (injection attempts), and option filtering when parsing.
- `tests/DatabaseAIAgent.IntegrationTests` runs each provider against **real** SQL Server 2022, PostgreSQL 17 and
  MySQL 8.4 containers (Testcontainers). The containers set up a least-privilege account exactly as the scripts
  above. These tests are skipped automatically when Docker is unavailable:

  ```bash
  dotnet test --project tests/DatabaseAIAgent.IntegrationTests
  ```
