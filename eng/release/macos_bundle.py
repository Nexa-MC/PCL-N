"""Immutable macOS identity/activation declarations, generated before bundle signing."""


def bundle_info(version, prefix):
    document_types = [
        dict(CFBundleTypeName="Modrinth Modpack", CFBundleTypeExtensions=["mrpack"],
             CFBundleTypeRole="Viewer", LSHandlerRank="Alternate", LSItemContentTypes=["org.modrinth.mrpack"]),
        dict(CFBundleTypeName="NexaCL Modpack", CFBundleTypeExtensions=["nexapack"],
             CFBundleTypeRole="Viewer", LSHandlerRank="Alternate", LSItemContentTypes=["org.nexacl.nexapack"]),
    ]
    types = [
        dict(UTTypeIdentifier="org.modrinth.mrpack", UTTypeDescription="Modrinth Modpack",
             UTTypeConformsTo=["public.zip-archive"], UTTypeTagSpecification={"public.filename-extension": ["mrpack"]}),
        dict(UTTypeIdentifier="org.nexacl.nexapack", UTTypeDescription="NexaCL Modpack",
             UTTypeConformsTo=["public.zip-archive"], UTTypeTagSpecification={"public.filename-extension": ["nexapack"]}),
    ]
    return dict(CFBundleName="NexaCL", CFBundleDisplayName="NexaCL", CFBundleIdentifier="org.nexacl.launcher",
                CFBundleExecutable="Nexa.Desktop", CFBundlePackageType="APPL", CFBundleIconFile="Launcher.icns",
                CFBundleShortVersionString=prefix, CFBundleVersion=prefix, NexaProductVersion=version,
                CFBundleURLTypes=[dict(CFBundleURLName="org.nexacl.launcher", CFBundleURLSchemes=["nexacl"], CFBundleTypeRole="Viewer")],
                CFBundleDocumentTypes=document_types, UTImportedTypeDeclarations=types,
                NSHighResolutionCapable=True, LSMinimumSystemVersion="12.0")
