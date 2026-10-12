# Contributing

Thanks for your interest in NvTgpHold.

## How changes get in

- `main` is protected: **no direct pushes**. Every change must come through a pull request.
- Every pull request needs an **approving review from the maintainer (@pablo906)** before it can be
  merged. Nothing is merged automatically.
- Fork the repository, create a branch, and open a pull request against `main`.

## Guidelines

- Keep changes focused; one topic per pull request.
- Describe what you changed, why, and **what hardware, BIOS and driver version you tested on**.
- Changes that alter power limits, register/ioctl values, or registry writes must explain the
  source of the values and the risk. Untested changes to these paths will not be accepted.
- Do not commit build output (`bin/`, `obj/`), binaries, credentials, or machine-specific paths.
- Make sure `dotnet build -c Release` succeeds.

## Reporting bugs

Open an issue with your laptop model, BIOS version, NVIDIA driver version, and the tool's status
line / balloon output. For security issues, see [SECURITY.md](SECURITY.md) instead.
