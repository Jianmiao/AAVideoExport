# Historical audited delivery tools

These four PowerShell files are byte-identical copies from the 0.2.9 delivery
bundle `artifacts/delivery-0.2.9-20261030/packages/bundle-0.2.9-cd65865fdd454ef4bc136729d87c0a66`.
They are test fixtures for legacy descriptor, launcher and rollback compatibility,
not the current installer or a production no-audit launch path.

The production deployment suite uses them only inside newly generated artifact
trees containing synthetic assemblies; it never launches AA or writes a real
installation. Preserve these historical files rather than modernizing their
behavior when changing the current delivery tools.
