#pragma once

// {C9E2689D-69D4-4A4F-85B0-9F7CFA6F3726}
// CLSID for ExplorerCommandHandler. Generated once; must stay stable across builds — it's
// the identity Explorer/registry use to find this handler. Kept as both a GUID and its
// registry-key string form (must match __uuidof(ExplorerCommandHandler) in ExplorerCommand.h)
// so Registration.cpp doesn't need to format one at runtime.
static constexpr GUID CLSID_ExplorerCommandHandler =
{ 0xc9e2689d, 0x69d4, 0x4a4f, { 0x85, 0xb0, 0x9f, 0x7c, 0xfa, 0x6f, 0x37, 0x26 } };

inline constexpr wchar_t CLSID_ExplorerCommandHandlerString[] = L"{C9E2689D-69D4-4A4F-85B0-9F7CFA6F3726}";
