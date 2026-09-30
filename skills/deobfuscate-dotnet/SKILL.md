---
name: Deobfuscate .NET
description: Deobfuscate .NET binaries loaded in dnSpy via MCP tools. Use this skill whenever the user wants to decrypt strings, rename obfuscated symbols, simplify control flow, detect proxy calls, remove anti-tamper protections, or make obfuscated .NET code readable. Also trigger when the user mentions deobfuscation, string encryption, obfuscated names, control flow obfuscation, code virtualization, or wants to analyze/patch a protected .NET binary — even if they don't explicitly say "deobfuscate."
---

## Deobfuscate .NET Binary

You have 38 MCP tools for interacting with dnSpy. Your job is to make obfuscated .NET code readable and, when requested, patch it in-place. You decide which tools to use and in what order based on what you discover — there is no fixed pipeline.

### Addressing Model — tokens, never names

Every element (type, method, field, property, event, namespace, resource, assembly reference) is addressed by its **.NET metadata token**, not by name. This matters most here: deobfuscation targets binaries where names are meaningless AND non-unique (thousands of types called `a`, `b`, `aa`), so a name lookup silently hits the wrong element.

The loop is always: **discover → get token → address by token**.

* **Discovery tools** take patterns and return tokens: `search_types`, `search_methods`, `search_strings`, `grep`, `assembly_list_types`, `assembly_list_namespaces`, `get_type_members`, `get_fields`, `get_properties`, `get_resources`, `get_enum_values`, `search_constants`, `get_selected_node`.
* **Every other tool** takes `token` (`"0x06000001"` or decimal `100663297`) plus an optional `moduleMvid` — and returns the identity back.
* Every payload contains `token`/`tokenHex`/`moduleMvid` (the immutable address) **and** `name`/`fullName` (mutable metadata, marked `"nameIsMutable": true`).
* A rename never changes the token, so keep reusing the token you already have after a rename — no re-discovery.
* Two loaded binaries always share monomorphic tokens (`0x02000001`). If a token exists in more than one loaded module the call fails and asks for `moduleMvid`; every discovery payload includes it, so pass it along.
* Passing a name or signature to a token parameter fails with `"ok": false` and `Invalid metadata token …`. That is deliberate — fall back to a search, never retry with a longer name.
* **Namespaces** have no metadata row: a namespace is identified by its *anchor type* (lowest TypeDef token in it). `assembly_list_namespaces` emits that anchor, and `rename_namespace` accepts the token of **any** TypeDef inside the namespace you want to rename.

### Mental Model

Obfuscation hides intent. Deobfuscation reveals it. Think in three phases:

1. **Recon** — What am I looking at? What protections are present?
2. **Analyze** — How does each protection work? Where are the weak points?
3. **Transform** — Rename, decrypt, simplify, or patch to restore readability.

You may cycle through these phases multiple times as understanding deepens.

### Identifying Obfuscation

Before acting, understand what you're dealing with. These patterns tell you which protections are active:

**Obfuscator signatures** — Check assembly-level attributes and metadata:
- `get_attributes` with `targetKind="assembly"` (or on any member token) — look for `ObfuscatedByAttribute`, `ConfusedByAttribute`, `DotfuscatorAttribute`, `CryptoObfuscator`, `SmartAssembly`, `BabelNet`, `AgileDotNet`, `Eazfuscator`, `ILProtector`, `.NET Reactor`
- `assembly_overview` — unusual type counts (hundreds of types with gibberish names)
- `get_metadata` — check for packed or modified PE headers

**String encryption** — Look for:
- `search_strings` returns very few or zero strings in an assembly that clearly uses string literals
- Decompiled code calls a helper method like `Convert.FromBase64String`, a static decrypt method, or `Encoding.GetString` with computed byte arrays instead of literal strings
- The same decrypt helper appears in many methods

**Symbol renaming** — Look for:
- Type/method/field names that are Unicode gibberish, single characters, or sequential patterns (`Class0001`, `method_0`)
- `search_types "regex:^[^a-zA-Z]+$"` finds types with no alphabetic characters in their name
- Namespaces that are empty or contain unprintable characters

**Control flow obfuscation** — Look for:
- `get_method_il` shows excessive `switch`/`br`/`brtrue`/`brfalse` with no clear logic
- Decompiled methods that are long spaghetti with many goto-like structures
- Opaque predicates: conditions that always evaluate the same way but look complex

**Proxy calls** — Look for:
- `get_callees` shows calls to small wrapper methods instead of direct API calls
- Wrapper methods contain a single `call` or `callvirt` to the real target
- Delegate fields initialized at startup that replace direct method calls

**Anti-tamper/anti-debug** — Look for:
- `search_methods` finds methods referencing `Debugger.IsAttached`, `Debugger.IsLogging`, `Environment.HasShutdownStarted`
- `search_strings` finds strings like "tamper", "integrity", "debug", "virtual machine"
- Hash validation loops in static constructors

### Decision Guide

Based on what you identified, choose your approach. You can combine multiple strategies.

#### String Decryption

The goal: replace `Decrypt("abcde")` with the plaintext `"result"`.

How to decide:
- If the encryption is **simple** (base64, XOR with known key, ROT) — compute the plaintext yourself, then use `update_method_body` to patch.
- If the encryption is **moderate** (custom algorithm with visible logic) — `decompile_method` the decryptor, understand the algorithm, compute the result, then patch.
- If the encryption is **complex** (AES, RSA, key derived at runtime) — trace the key derivation with `get_xrefs_to` and `get_callees`, find the key source via `search_constants` or `get_fields`, then attempt decryption.

Bulk approach: identify the decrypt helper via `get_xrefs_to`, then trace all its callers with `get_callees` to find every encrypted string site.

Always verify by `decompile_method` after patching to confirm the plaintext appears correctly.

#### Symbol Renaming

The goal: replace gibberish names with meaningful ones based on context clues.

How to decide names:
- Method body reveals purpose — a method that calls `File.ReadAllText` and returns a string is probably `ReadFileContent`
- Return types and parameter types are strong hints — `bool CheckLicense(string key)` not `bool method_0(string s)`
- Xref context helps — callers tell you how the method is used
- Field names paired with their usage patterns

Tools: `rename_method` (one MethodDef token), `rename_class` (one TypeDef token), `rename_namespace` (any TypeDef token inside the namespace). All support `dryRun=true` for preview, and all answer with the SAME token plus the NEW name — the token is the identity and survives the rename, so re-use it for the next call instead of searching again.

Because renaming is token-addressed, there is no "rename everything called `a`" shortcut. Enumerate the candidates with `search_methods`/`search_types` (each hit carries its token), decide per element, then rename each token. That per-token loop is what keeps you from renaming an unrelated `a` in another class.

Strategy: rename from bottom up (fields/properties first, then methods, then classes, then namespaces). This way when you rename a class, its members already make sense.

#### Control Flow Simplification

The goal: remove fake branches and dead code to reveal the real logic.

This is the hardest type. Your approach:
1. `get_method_il` to see the raw IL — identify the real blocks vs junk blocks
2. `decompile_method` to see what dnSpy already simplifies — sometimes the decompiler handles it
3. If still obfuscated, use `update_method_body` to write a clean C# version of what the method actually does
4. Verify with `decompile_method` after patching

Be cautious: control flow transformations can be subtle. If you're unsure what a method does, trace its callees to understand intent before rewriting.

#### Proxy Call Resolution

The goal: replace `proxy.Invoke(args)` with the direct `RealTarget(args)` call.

1. `decompile_method` on the proxy to see what it actually calls
2. `get_xrefs_to` on the real target to find all proxy sites
3. `update_method_body` on each caller to replace proxy calls with direct calls

#### Anti-Tamper Removal

The goal: disable checks that prevent analysis.

1. `search_methods` to find check methods
2. `decompile_method` to understand the check logic
3. `update_method_body` to either return a safe value (e.g., `return true;` for license checks) or replace the method with a no-op
4. Always verify the binary still runs after patching

### Patching Safety

`update_method_body` compiles C# via Roslyn and replaces IL in memory. Follow these rules:

- **Always `decompile_method` first** to see the current code
- **Always use `dryRun=true`** on the first attempt — it shows the compiled IL without applying
- **Verify** by `decompile_method` again after patching
- **One method at a time** — don't batch patches until you're confident
- If Roslyn compilation fails, try simplifying the C# — avoid complex expressions, use temporary variables

### Tool Quick Reference

| Need | Start with | Then |
|------|-----------|------|
| What's loaded? | `list_loaded_assemblies` | `assembly_overview` |
| Find obfuscator | `get_attributes` | `get_metadata` |
| Find strings | `search_strings` | `grep` |
| Find encrypted strings | `decompile_method` | `get_xrefs_to` on decrypt helper |
| Understand method | `decompile_method` | `get_method_il` |
| Trace calls | `get_callees` | `get_xrefs_to` |
| Find types by name | `search_types` | `get_type_members` |
| Find methods | `search_methods` | `get_method_signatures` |
| Find constants/keys | `search_constants` | `get_fields` |
| Rename | `rename_method` / `rename_class` / `rename_namespace` | `refresh_ui` |
| Patch IL | `update_method_body` (dry_run first) | `decompile_method` to verify |
| After changes | `refresh_ui` | verify in dnSpy UI |

### Workflow Tips

- Start with `list_loaded_assemblies` to know what's in scope and to collect the `moduleMvid` values. If multiple assemblies are loaded, scope searches with the `assembly` parameter, and always pass `moduleMvid` alongside a token when more than one binary is loaded.
- Keep the tokens you discovered in your working notes. Every follow-up call (`decompile_method`, `get_method_il`, `get_callees`, `get_xrefs_to`, `rename_method`, `update_method_body`, ...) needs the token, and re-searching after every step wastes the discovery you already did.
- When you encounter a method with many `<>` compiler-generated names (`<>c__DisplayClass`, `<>g__`), these are usually lambda/local function artifacts, not obfuscation — treat them differently.
- Some obfuscators inject dummy types or methods. Use `get_type_hierarchy` to spot types with no base (other than `Object`) and no interfaces — these are often junk.
- `search_types` and `search_methods` accept a `regex:` prefix (`regex:^[^a-zA-Z]+$`) — powerful for gibberish-name patterns. `search_strings` matches substrings only; combine it with `grep` scope `strings` for broader literal sweeps.
- After any rename or patch, call `refresh_ui` so dnSpy's tree view updates.
