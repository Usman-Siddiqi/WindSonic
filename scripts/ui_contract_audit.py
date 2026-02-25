#!/usr/bin/env python3
"""Static UI contract audit for WindSonic WPF view.

Checks:
1) XAML bindings that target MainWindowViewModel exist.
2) XAML event handlers are implemented in MainWindow.xaml.cs.
"""
from __future__ import annotations

import pathlib
import re
import sys
from dataclasses import dataclass

ROOT = pathlib.Path(__file__).resolve().parents[1]
XAML_FILE = ROOT / "WindSonic.App" / "MainWindow.xaml"
VIEWMODEL_FILE = ROOT / "WindSonic.App" / "ViewModels" / "MainWindowViewModel.cs"
CODEBEHIND_FILE = ROOT / "WindSonic.App" / "MainWindow.xaml.cs"

IGNORED_BINDING_ROOTS = {
    # Row item view models/models used in item templates.
    "Title",
    "Subtitle",
    "DurationLabel",
    "Summary",
    "Name",
    "Track",
    "PlayedAtLabel",
    "EngineModeLabel",
}

EVENT_ATTRIBUTES = (
    "Click",
    "Loaded",
    "SourceInitialized",
    "PreviewKeyDown",
    "MouseDoubleClick",
    "PreviewMouseLeftButtonDown",
    "PreviewMouseLeftButtonUp",
    "PreviewKeyUp",
    "RequestNavigate",
)


@dataclass(frozen=True)
class AuditResult:
    missing_bindings: list[str]
    missing_handlers: list[str]


def _read(path: pathlib.Path) -> str:
    return path.read_text(encoding="utf-8")


def _collect_binding_roots(xaml: str) -> set[str]:
    roots: set[str] = set()
    for match in re.finditer(r"\{Binding\s+([^}]+)\}", xaml):
        expr = match.group(1).strip()
        if expr.startswith(("RelativeSource", "ElementName")):
            continue
        path = expr.split(",", 1)[0].strip()
        if not path or path == ".":
            continue
        roots.add(path.split(".", 1)[0])
    return roots


def _collect_public_property_names(csharp: str) -> set[str]:
    names: set[str] = set()
    for match in re.finditer(r"public\s+[\w<>?]+\s+(\w+)\s*(?:=>|\{)", csharp):
        names.add(match.group(1))
    return names


def _collect_declared_methods(csharp: str) -> set[str]:
    methods: set[str] = set()
    for match in re.finditer(
        r"\b(?:private|public|internal|protected)\s+(?:async\s+)?(?:void|Task|Task<[^>]+>)\s+(\w+)\s*\(",
        csharp,
    ):
        methods.add(match.group(1))
    return methods


def _collect_xaml_handlers(xaml: str) -> set[str]:
    handlers: set[str] = set()
    for attr in EVENT_ATTRIBUTES:
        for match in re.finditer(rf"\b{attr}\s*=\s*\"([^\"]+)\"", xaml):
            handlers.add(match.group(1))
    return handlers


def run_audit() -> AuditResult:
    xaml = _read(XAML_FILE)
    vm = _read(VIEWMODEL_FILE)
    codebehind = _read(CODEBEHIND_FILE)

    binding_roots = _collect_binding_roots(xaml)
    vm_props = _collect_public_property_names(vm)

    missing_bindings = sorted(
        root
        for root in binding_roots
        if root not in vm_props and root not in IGNORED_BINDING_ROOTS
    )

    handlers = _collect_xaml_handlers(xaml)
    methods = _collect_declared_methods(codebehind)
    missing_handlers = sorted(handler for handler in handlers if handler not in methods)

    return AuditResult(missing_bindings=missing_bindings, missing_handlers=missing_handlers)


def main() -> int:
    result = run_audit()

    print("WindSonic UI contract audit")
    print(f"- XAML file: {XAML_FILE.relative_to(ROOT)}")
    print(f"- ViewModel file: {VIEWMODEL_FILE.relative_to(ROOT)}")
    print(f"- Code-behind file: {CODEBEHIND_FILE.relative_to(ROOT)}")

    ok = True
    if result.missing_bindings:
        ok = False
        print("\n[FAIL] Missing MainWindowViewModel binding targets:")
        for item in result.missing_bindings:
            print(f"  - {item}")
    else:
        print("\n[PASS] All MainWindow-level binding targets were found in MainWindowViewModel.")

    if result.missing_handlers:
        ok = False
        print("\n[FAIL] Missing event handlers in MainWindow.xaml.cs:")
        for item in result.missing_handlers:
            print(f"  - {item}")
    else:
        print("[PASS] All event handlers referenced in XAML exist in MainWindow.xaml.cs.")

    return 0 if ok else 1


if __name__ == "__main__":
    raise SystemExit(main())
