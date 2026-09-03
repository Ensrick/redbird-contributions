# Security policy

Native detours operate inside the host process and invalid signatures or addresses can cause memory corruption. 
Treat all hook targets and calling-convention declarations as trusted configuration.

Report suspected vulnerabilities privately through the GitLab project's security-reporting facility. 
Do not include working exploits, credentials, or private target binaries in a public issue.

Security fixes are supported for the latest released major version. 
Reports should include the package version, target architecture, operating system, runtime version, and a minimal reproduction that does not contain third-party proprietary code.
