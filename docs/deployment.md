# Hosting and local operation

## Free deployment

The application runs as one Docker web service on Render Free. PostgreSQL is a dedicated Neon Free project, version 17, in Frankfurt. Do not use Render's expiring free database for the permanent demo.

1. Create a dedicated Neon Free database. Copy its connection details privately.
2. Create a Render **Web Service** from this public repository using the Docker runtime and **Free** instance type, in Frankfurt.
3. Set `ASPNETCORE_ENVIRONMENT=Production` and `ConnectionStrings__Desk` in Render's environment settings:

   `Host=<pooled-host>;Database=kitdesk;Username=<role>;Password=<secret>;SSL Mode=VerifyFull;Maximum Pool Size=10`

4. Use `/healthz` as the health path. The image binds to port 8080, applies pending migrations, creates missing demo roles and starts ASP.NET Core.
5. Run browser checks against the public URL. Check two independent workspaces, checkout conflicts and a normal page reload.

`render.yaml` records the equivalent configuration. Automatic deployment is disabled; deploy a commit only after CI passes. No payment details, paid add-ons, persistent disks or keep-alive traffic are required by this design. Stop if the account requires an upgrade.

## Limits and credentials

Render redirects public HTTP to HTTPS and then forwards HTTP to the container ([platform documentation](https://render.com/docs/web-services#connect-to-your-web-service)). In Production with Render's built-in `RENDER=true` setting, the app restores the HTTPS scheme before authentication and antiforgery run. Cookies remain Secure. It does not consume forwarded host, protocol or client-IP headers. Only enable this platform setting behind Render's HTTPS entry point; another host needs its own trusted-proxy configuration.

Render Free can sleep after 15 idle minutes and takes time to wake up. Its filesystem is ephemeral. Equipment, loans and Data Protection keys live in PostgreSQL rather than local files. Neon can also suspend idle compute. Free quotas are account-dependent and availability is not guaranteed.

Never commit a database connection string, hosting token or Data Protection key. Use platform environment settings or local environment variables. The demo does not accept real student details or connect to email, payments or external identity providers.

The application deliberately reads the immediate network peer for admission limits rather than trusting a client-supplied forwarding header. A hosting proxy can therefore make visitors share the five-start allowance. Workspace mutation limits are independent. The database's active-workspace cap is shared across app instances; rate counters are process-local.

## Migration and recovery

`dotnet run --project src/KitDesk -- --migrate` only applies pending migrations and creates missing roles. The container executes the same migration path before serving requests. A failed migration stops startup; it does not reset the database.

If a deploy fails, inspect its logs and restore the last working code version. Do not drop tables or remove persistent volumes as a deployment fix. Neon recovery availability depends on its plan; a production backup/restore policy is outside this demo's evidence.
