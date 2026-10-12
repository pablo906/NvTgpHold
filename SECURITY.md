# Security Policy

NvTgpHold runs with administrator rights and talks to kernel drivers (NVIDIA, ASUS ATK ACPI) and
the system registry, so security issues matter.

## Reporting a vulnerability

Please **do not open a public issue** for security problems. Instead, use GitHub's private
vulnerability reporting: go to the repository's **Security** tab and choose
**Report a vulnerability**.

Include a description, steps to reproduce, and the potential impact. You can expect an initial
response when the maintainer is next available; fixes are released on a best-effort basis.

## Supported versions

Only the latest commit on `main` is supported.

## Downloads

Only build from source or use releases published in this repository. Do not run binaries of this
tool obtained from anywhere else.
