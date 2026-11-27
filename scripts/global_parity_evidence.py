#!/usr/bin/env python3
"""Run the independently selected credential-free cross-domain tests using shared receipts."""
import argparse
from pathlib import Path
from realtime_parity_evidence import run

# Only local schema, synthetic HTTP, pure tool-registry and temp-file tests.
CLASSES = (
    "AppServerModelSchemaTests", "AppServerThreadLifecycleContractTests",
    "CodexAccountRateLimitsTests", "CodexModelCatalogTests",
    "CodexToolRegistryTests", "CodexFileSystemTests",
    "ThreadArchiveStoreTests", "CodexModelCatalogRpcTests",
    "CodexPermissionProfileCatalogTests", "CodexMcpClientTests",
    "CodexMcpConnectionPoolTests", "CodexMcpHttpTransportTests",
    "CodexToolRequestLedgerTests", "CodexAsyncUserMessageTests",
)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--host-admission", action="store_true")
    args = parser.parse_args()
    return run(args.output.resolve(), args.host_admission, offline_classes=CLASSES, scope="global")


if __name__ == "__main__":
    raise SystemExit(main())
