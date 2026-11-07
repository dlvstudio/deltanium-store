# Deltanium Store

A secure file storage node that stores encrypted posts/files and exposes APIs for metadata and block uploads, as well as follower feeds. Integrates with the central API for store discovery and following relationships.

## Features

- Secure file upload with cryptographic signature verification
- Request timestamp validation to prevent replay attacks
- Support for multipart/form-data file uploads
- Detailed logging for debugging and monitoring

## API Endpoints

### File APIs (`/api/file`)

- `POST /api/file/upload`
  - Legacy multipart upload endpoint (still available).

- `POST /api/file/upload-metadata-raw`
  - Uploads metadata as raw JSON (used for posts and files). Required headers:
    - `X-User-PubKey`: Author public key
  - Required JSON fields include: `fileId`, `firstBlockId`, `type` (e.g., `post` or `file`), `shareType`, `encryptedType`, optional `recipientPubKey`, `encryptedKey`.
  - Followers-only (PRE hybrid) metadata also includes:
    - `ownerPubKey` (author’s public key)
    - `policyTag` (e.g., `followers:2025`)
    - `capsuleFor = "tag"`, `policyScheme = "CPRE"`
    - `encapsulatedForRecipient` (PRE capsule for followers; same capsule reused)

- `POST /api/file/upload-file-block-raw`
  - Uploads a content block as raw binary. Required headers:
    - `X-User-PubKey`
    - `X-File-Id`
    - `X-Block-Id`
    - `X-Block-Index` (int)
  - Validates that metadata for `X-File-Id` exists before accepting the block.

- `GET /api/file/exists/{fileId}`
  - Checks if file metadata exists.

- `GET /api/file/posts`
  - Lists posts for the caller, excluding followers‑only posts (requires `X-User-PubKey`).

### Post APIs (`/api/post`)

- `GET /api/post/user/{userPubKey}`
  - List posts of a specific user (filtered by `type = post`).

- `GET /api/post/timeline`
  - Personal timeline: lists caller’s own posts. Requires `X-User-PubKey`.

- `GET /api/post/following-feed`
  - Aggregates posts from users the caller follows using central API. Requires `X-User-PubKey`.

- `GET /api/post/following-feed-detailed`
  - Like `following-feed` but attempts to include post content/author info.

- `GET /api/post/followers-feed`
  - Returns posts with `shareType = followers` that the caller can access.
  - Includes PRE hybrid fields when present: `ownerPubKey`, `policyTag`, `capsuleFor`, `policyScheme`, `encapsulatedForRecipient`.

## Security

- All sensitive content is stored encrypted; metadata can be minimized for zero‑knowledge (e.g., omit file name/size/MIME in plaintext).
- Requests include `X-User-PubKey`; signature/timestamp verification can be enforced (legacy multipart demonstrates pattern). Raw endpoints rely on upstream auth and per‑recipient encrypted keys for access control.

### PRE Security Model

**Key Security Properties:**
1. **Encapsulation is public**: Anyone can create a capsule with a public key, but this doesn't compromise security because:
   - Fake capsules cannot decrypt real content (content encrypted with real symmetric key K)
   - Only the author (with `sk_A`) can generate valid rekeys
   - Only followers (with their `sk_B`) can decapsulate re-encrypted capsules

2. **Rekey generation requires author's secret key**:
   - `rk_{A→B} = generateRekey(sk_A, pk_B, tag)`
   - Only A (with `sk_A`) can create valid rekeys for their followers
   - Attackers without `sk_A` cannot create valid rekeys

3. **Zero-knowledge property**:
   - Store doesn't know who has access to PRE posts (no `recipientPubKey` in metadata)
   - Store only sees `ownerPubKey` and `policyTag`
   - Access control enforced by Central API (follow relationships + PoP)

4. **Authorization flow**:
   - Followers must prove possession of their secret key (PoP: sign nonce)
   - Central API verifies follow relationship before issuing rekeys
   - Rekeys are scoped to specific tags (e.g., `followers:2025`) and optionally file IDs

## Configuration

```json
{
  "CentralApi": { "BaseUrl": "http://localhost:5002", "Timeout": 30 },
  "InternalApi": { "Token": "" },
  "NodeRegistration": {
    "ApiUrl": "http://localhost:5002",
    "Endpoint": "http://localhost:5001",
    "Voucher": ""
  },
  "Storage": { "Provider": "MySql" },
  "ConnectionStrings": { "Default": "" }
}
```

**Required for local/prod (do not commit real values):**

| Setting | Source |
|---------|--------|
| MySQL connection | User Secrets `deltanium-store-local-secrets` |
| Registration voucher | env `STORE_VOUCHER` or `NodeRegistration:Voucher` (user-secrets) |
| Internal API token | env `INTERNAL_API_TOKEN` or `InternalApi:Token` (same value as Central API) |

If voucher is missing, the node starts but **skips** registration (warning logged).

## Development Setup

1. Install .NET 8 SDK
2. Set secrets / env (connection, voucher, internal token)
3. Run:
```bash
cd deltanium-store
dotnet restore
dotnet run
```

Default listen URL comes from `NodeRegistration:Endpoint` / Kestrel config (often `http://localhost:5001`).

## Integration with Central API

- Following list / user info via Central API (`CentralApi:BaseUrl`).
- PRE `validate-token` calls include `X-Internal-Token`.
- App discovers stores via `GET /api/storenode/list`, may upload the **same** encrypted `fileId` to multiple stores (N-of-M), then indexes with `POST /api/storedfile/register-replicas`.

## Detailed Flow: How Followers Access Posts

### Scenario: User A has followers B and C, creates post X

#### Step 1: A Creates Post X (with `shareType = "followers"`)

**Option 1 (ECIES):**
```
1. A's device generates symmetric key K
2. Encrypts content blocks: Enc_K(postContent)
3. For each user [A, B, C]:
   - Creates metadata entry with:
     - fileId (unique per user)
     - encryptedKey = ECIES(K, pk_user)
     - recipientPubKey = pk_user
4. Uploads 3 metadata entries + content blocks to Store
```

**Option 2 (PRE):**
```
1. A's device generates symmetric key K
2. Encrypts content blocks: Enc_K(postContent)
3. Creates PRE capsule: capsule = encapsulateWithTag(pk_A, "followers:2025", K)
4. Creates ONE metadata entry with:
   - fileId (for A)
   - encryptedKey = ECIES(K, pk_A)  // For A's direct access
   - ownerPubKey = pk_A
   - policyTag = "followers:2025"
   - capsuleFor = "tag"
   - encapsulatedForRecipient = capsule  // For followers
5. Uploads 1 metadata entry + content blocks to Store
6. A's device generates rekeys for followers:
   - rk_{A→B} = generateRekey(sk_A, pk_B, "followers:2025")
   - rk_{A→C} = generateRekey(sk_A, pk_C, "followers:2025")
7. Uploads rekeys to Central API
```

#### Step 2: B Reads Post X

**Option 1 (ECIES):**
```
1. B queries: GET /api/post/followers-feed
2. Store returns posts where recipientPubKey = pk_B
3. B finds post X with fileId_B
4. B downloads metadata block: GET /api/file/block/{fileId_B}/0
5. B decrypts: K = ECIES_Decrypt(encryptedKey_B, sk_B)
6. B downloads content blocks and decrypts with K
```

**Option 2 (PRE):**
```
1. B queries: GET /api/post/followers-feed
2. Store returns posts with shareType="followers" and ownerPubKey in B's following list
3. B finds post X (with PRE fields: capsuleFor="tag", policyTag="followers:2025")
4. B requests rekey from Central API:
   - GET /api/policy/fetch-rekey?following=pk_A&tag=followers:2025
   - Central verifies: B follows A? (yes) + PoP (yes)
   - Central returns: rk_{A→B}
5. B's device:
   - Re-encrypts: capsule_for_B = reencrypt(capsule, rk_{A→B})
   - Decapsulates: K = decapsulate(capsule_for_B, sk_B, "followers:2025")
6. B downloads content blocks and decrypts with K
```

#### Step 3: New Follower D Follows A

**Option 1 (ECIES):**
```
- A's device must create new metadata entry for D:
  - fileId_D (unique)
  - encryptedKey = ECIES(K, pk_D)
  - recipientPubKey = pk_D
- Uploads new metadata entry to Store
- D can now access post X
```

**Option 2 (PRE):**
```
- A's device generates rekey: rk_{A→D} = generateRekey(sk_A, pk_D, "followers:2025")
- Uploads rekey to Central API
- No new metadata entry needed!
- D can now access post X using existing capsule
```

## Post Storage Model (Overview)

- A post is a file with `type = "post"` in metadata.
- The app uploads:
  - Metadata (raw JSON) including `fileId`, `firstBlockId`, `shareType`, `encryptedType`, and per‑recipient fields for followers/private sharing (e.g., `recipientPubKey`, `encryptedKey`).
  - For followers-only PRE hybrid posts and attached media, metadata additionally includes `ownerPubKey`, `policyTag`, `capsuleFor = tag`, `policyScheme = CPRE`, `encapsulatedForRecipient`.
  - One or more content blocks (raw binary) identified by `blockId`/`blockIndex`.
- Followers feed endpoints include the PRE hybrid fields to enable client-side re-encryption.

## Sharing Mechanisms: ECIES vs PRE

The system supports two methods for sharing encrypted posts with followers:

### Option 1: ECIES (Per-Recipient Encryption)

**How it works:**
- When author A creates a post with `shareType = "followers"`, the system creates a separate metadata entry for each follower (A, B, C, ...).
- Each metadata entry contains:
  - `recipientPubKey`: The follower's public key
  - `encryptedKey`: Symmetric key K encrypted with the follower's public key using ECIES
  - Unique `fileId` and `firstBlockId` per recipient
- Content blocks are uploaded once and shared by all recipients.

**Characteristics:**
- ✅ Simple implementation
- ❌ Creates N metadata entries (one per follower)
- ❌ Store knows who has access (via `recipientPubKey`)
- ❌ New followers require creating new metadata entries

**Example flow:**
```
A creates post → Creates 3 metadata entries (A, B, C) → Upload
B queries feed → Finds post with recipientPubKey=pk_B → Decrypts with sk_B → Reads content
```

### Option 2: PRE (Proxy Re-Encryption) - Tag-Based

**How it works:**
- When author A creates a post with `shareType = "followers"` and PRE enabled (`capsuleFor = "tag"`), the system:
  1. Creates a single PRE capsule using `encapsulateWithTag(pk_A, tag, K)` where `tag = "followers:YYYY"`
  2. Stores only ONE metadata entry (for A) containing:
     - `ownerPubKey`: Author's public key
     - `policyTag`: e.g., `"followers:2025"`
     - `capsuleFor = "tag"`, `policyScheme = "CPRE"`
     - `encapsulatedForRecipient`: The PRE capsule (same for all followers)
     - `encryptedKey`: ECIES(K, pk_A) for direct author access
- Content blocks are uploaded once and shared by all followers.

**Rekey Generation:**
- When A has a new follower B, A's device generates a rekey:
  ```
  rk_{A→B} = generateRekey(sk_A, pk_B, "followers:2025")
  ```
- The rekey is uploaded to Central API and stored with metadata: (A, B, tag, timestamp).
- **Security**: Only A (with `sk_A`) can generate valid rekeys.

**Follower Access Flow:**
```
1. B queries following feed → Finds post of A (with PRE fields)
2. B requests rekey from Central API: GET /api/policy/fetch-rekey?following=pk_A&tag=followers:2025
3. Central API verifies:
   - B follows A? (yes)
   - B has PoP (proof of possession of sk_B)? (yes)
4. Central returns: rk_{A→B}
5. B's device:
   - Re-encrypts: capsule_for_B = reencrypt(capsule, rk_{A→B})
   - Decapsulates: K = decapsulate(capsule_for_B, sk_B, tag)
   - Decrypts content blocks with K
```

**Characteristics:**
- ✅ Only 1 metadata entry (zero-knowledge: Store doesn't know who has access)
- ✅ New followers don't require new metadata entries (just need rekey)
- ✅ Better scalability and privacy
- ❌ More complex implementation (requires rekey management)

**Security Notes:**
- **Encapsulation is public**: Anyone can create a capsule with `pk_A`, but this is not a security issue because:
  - Fake capsules cannot decrypt real content (content encrypted with real K)
  - Only A (with `sk_A`) can generate valid rekeys
  - Only B (with `sk_B`) can decapsulate re-encrypted capsules
- **Rekey generation**: Only A (with `sk_A`) can create valid rekeys for their followers
- **Authorization**: Central API enforces follow relationships and proof-of-possession

### Comparison

| Feature | Option 1: ECIES | Option 2: PRE |
|---------|----------------|---------------|
| Metadata entries | N entries (1 per user) | 1 entry (author only) |
| Store knows access | Yes (`recipientPubKey`) | No (zero-knowledge) |
| New follower | Create new metadata entry | Just generate rekey |
| Scalability | Poor (N entries) | Good (1 entry) |
| Privacy | Lower | Higher |
| Complexity | Simple | More complex |

### PRE Re-Encryption Endpoint

- `POST /api/file/pre/reencrypt`
  - Allows followers to request re-encryption of PRE capsules
  - Validates token from Central API (proves follow relationship and PoP)
  - Performs re-encryption using PRE library
  - Returns transformed capsule for the follower
  - **Note**: Store performs re-encryption but never sees the symmetric key K

## Debugging

The service includes detailed logging for signature verification:
- All request headers are logged
- Signature verification steps are logged
- Body hash calculation is logged
- Timestamp validation is logged

Enable Debug level logging in appsettings.json for more detailed logs:
```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Debug"
    }
  }
}
```