# Third-party software in the HR Payroll package

| Component | Licence |
|---|---|
| .NET runtime inside `migrate.exe` (the web app itself uses the server's ASP.NET Core 10 Hosting Bundle) | MIT |
| Entity Framework Core, ASP.NET Core Identity, Microsoft.Data.SqlClient | MIT |
| ClosedXML (Excel exports) | MIT |
| QuestPDF (PDF exports) | QuestPDF Community licence: free for organisations with less than USD 1M annual gross revenue; see questpdf.com |
| QRCoder (two-factor QR codes) | MIT |
| Serilog, Serilog.AspNetCore and its sinks (logging) | Apache 2.0 |
| Bootstrap 5.3.3, Bootstrap Icons 1.13.1 | MIT |
| jQuery 3.7.1, jQuery Validation, jQuery Validation Unobtrusive | MIT |
| Plus Jakarta Sans font (web and PDF) | SIL Open Font License 1.1 (`OFL.txt`) |

7-Zip, which the backup script uses for encrypted copies, is not included: install it yourself from https://www.7-zip.org (GNU LGPL).
