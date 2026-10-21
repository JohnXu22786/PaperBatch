# V1 IPP protocol-fault lab

This lab supplies a reproducible Linux IPP Everywhere receiver for development and fault testing. It builds the upstream OpenPrinting CUPS `ippeveprinter` and `ipptool` utilities from the exact v2.4.10 source archive in an isolated Docker container. DNS-SD, TLS, and PAM support are disabled in the build; the receiver binds to loopback and does not need Avahi or D-Bus. It is protocol emulation only; it is not a Windows print-driver or GDI test, an OEM/IPP driver test, an Office/WPS coexistence test, or evidence of output from a physical printer. It does not close G1.

## Pinned environment and license inventory

| Component | Pin | Source and license record |
| --- | --- | --- |
| Base image | `ubuntu:24.04@sha256:a2d43bffc46670d7108bb3c0468c67c5557c4c92e83e1e34cbff1322ad1b94dc` (`linux/amd64`) | Official Ubuntu image; per-package copyright metadata is captured during the build. |
| IPP utilities | OpenPrinting CUPS `v2.4.10`, archive SHA-256 `f51b9edd631db1830b967101b51f0045c8c239ae799dff89f0399f3e47a95c02` | Official [source archive](https://github.com/OpenPrinting/cups/archive/refs/tags/v2.4.10.tar.gz), built with `--with-dnssd=no --with-tls=no --with-pam-module=no`. CUPS is Apache-2.0 with the CUPS linking exception and third-party notices; the image includes upstream [`LICENSE`](https://github.com/OpenPrinting/cups/blob/v2.4.10/LICENSE), [`NOTICE`](https://github.com/OpenPrinting/cups/blob/v2.4.10/NOTICE), and [`README`](https://github.com/OpenPrinting/cups/blob/v2.4.10/README.md). |
| Build package set | Exact names and versions in [`tools/ipp-lab/build-packages.lock`](../../tools/ipp-lab/build-packages.lock) | Includes the pinned compiler, make, zlib headers, Python used for inventory generation, and their complete additional dependency closure. |
| Runtime package set | Exact names and versions in [`tools/ipp-lab/runtime-packages.lock`](../../tools/ipp-lab/runtime-packages.lock) | Includes Python 3.12.3 and its complete additional dependency closure. The runtime image also contains the pinned Ubuntu base-image package set. |
| Lab code and fixtures | This repository revision | Covered by the repository [LICENSE](../../LICENSE). The PDF fixture contains no user content. |

The image is pinned to the official Ubuntu Noble `linux/amd64` digest above. Both stages use Ubuntu Noble, Noble Updates, and Noble Security from the immutable snapshot at `20261018T000000Z` over HTTPS. The minimal Ubuntu base lacks a system CA bundle, so the Dockerfile disables TLS peer verification only for `snapshot.ubuntu.com`; apt still requires the repository's signed `InRelease` metadata and checks package hashes against that signature. The source also disables `Valid-Until` expiry checking because the snapshot is intentionally historical. Every added package version is listed exactly in its lock file, and the Dockerfile compares the installed package set with the pinned base plus that lock. A missing package version or a changed dependency set fails the build. The image includes `/opt/paperbatch/docs/runtime-packages.tsv`, `/opt/paperbatch/docs/runtime-license-inventory.json`, `/opt/paperbatch/docs/build-packages.tsv`, and `/opt/paperbatch/docs/build-license-inventory.json`. Each inventory records installed package versions, SHA-256 hashes of packaged copyright metadata where present, and declared licenses. The CUPS upstream source license and notice are included under `/opt/paperbatch/docs/cups/`. Inspect an inventory without mounting a host path with `docker run --rm --network none paperbatch-ipp-lab:local cat docs/runtime-license-inventory.json` after building the image.

## Run and reset

Requirements are Docker and a Linux/amd64 runtime. The runner builds the digest-pinned image and starts it with a network-none namespace, read-only root filesystem, a small ephemeral `/tmp`, no Linux capabilities, and resource limits:

```sh
tools/run_ipp_fault_lab.sh       # run success plus all fault scenarios and print redacted JSON traces
tools/run_ipp_fault_lab.sh test  # run the focused integration assertions
```

Each scenario starts a fresh `ippeveprinter`, a loopback-only fault proxy, and an isolated temporary spool directory. The receiver advertises no DNS-SD service, uses an ephemeral port, accepts only the synthetic PDF fixture, and writes only under the temporary directory. The runner always stops its receiver and proxy and removes that directory, including when a scenario fails. Docker's `--rm` removes the test container after exit. No host CUPS daemon, printer queue, home directory, USB device, or host path is mounted.

The lab sends a synthetic `Print-Job`, records its receipt, queries its job attributes, then uses `Create-Job`/`Get-Job-Attributes`/`Cancel-Job` for a pending synthetic job. `ippeveprinter` runs `/bin/true` on the synthetic spool file so the receipt job finishes promptly and a second pending job gives `Cancel-Job` a deterministic state to cancel. Fault scenarios send one `Print-Job` directly over a raw bounded socket so the client cannot transparently retry it after a transport failure; they perform only a later printer-attribute query to check recovery.

## Scenarios and classification

All faults apply once to the first `Print-Job`. They are deterministic and bounded. The proxy accepts only local `POST` requests with `Content-Length`; the bundled `ipptool` definitions select that transfer mode. Responses and request sizes have fixed limits. The maximum document is 512 KiB, the IPP request body is 528 KiB, an HTTP header is 8 KiB, and a response body is 592 KiB. Client timeouts are restricted to 1–5 seconds. HTTP parsing and each proxy handler share one absolute seven-second deadline across header reads, body reads, forwarding, and response reads, so a slow-drip peer cannot extend shutdown indefinitely. The trace is capped at 24 small events; captured `ipptool` output is capped at 64 KiB and is not included in results.

| Scenario | Injected behavior | Result classification |
| --- | --- | --- |
| `success` | Submit PDF, query the returned job, and cancel a separate pending synthetic job. | `accepted` |
| `delayed-response` | Forward the job, read the receiver response, then hold it beyond the client timeout. | `uncertain-timeout` |
| `connection-reset` | Forward the job and reset the client connection after the receiver responds. | `uncertain-transport` |
| `unavailable-503` | Return HTTP 503 without forwarding the request. The client cannot prove server-side non-acceptance from the response alone. | `uncertain-http` |
| `malformed-ipp` | Forward the job, then replace the IPP response body with a malformed one-byte response. | `uncertain-protocol` |
| `partial-upload` | Forward only half of the bounded IPP request body and reset the upstream connection. | `uncertain-transport` |

For each fault the JSON result records one `Print-Job` request, the expected uncertain classification, and a successful subsequent `Get-Printer-Attributes` recovery query. The health query shows the protocol endpoint can respond again; it does not determine whether an uncertain print job completed. Results omit client addresses, hostnames, local usernames, raw request/response bodies, and full command output.

## Evidence limits

The receiver and proxy validate IPP/HTTP request and response behavior only. They do not exercise a Windows print pipeline, a native or OEM driver, Office/WPS automation, actual queue routing, device state, or paper output. A successful protocol response is not proof of rendering fidelity, page count, physical output, or user-visible print completion. Treat every result as protocol-emulation evidence and keep G1 open until its native and physical requirements are met.
