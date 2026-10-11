# Deployment

HR Payroll v1 is deployed as a **portable release package for one Windows PC**, reachable only on that PC at `https://localhost:7443`.

- **Build the package:** run `build-package.ps1` in the repository root. It produces `dist\HRPayroll-v<version>-win-x64.zip`.
- **Install, update, back up, restore, admin reset, uninstall:** see [deploy/INSTALL.md](../deploy/INSTALL.md) and [deploy/restore.md](../deploy/restore.md). Both files also ship inside the package.

**Deferred** (owner decision, M10): hosting on IIS, or making the app reachable from other computers or the internet.

Before any such exposure, work through the checklist in [SECURITY-REVIEW.md](SECURITY-REVIEW.md#before-any-network-or-internet-exposure). It covers:
- bind address
- certificate for a real hostname
- firewall
- IIS and TLS
- forwarded headers
- a ZAP scan.

`src/HR.Web/web.config` is already prepared for IIS.
