# 🔒 Security Analysis: Comment Flow

## Flow Overview
1. **Post comment** (Flutter app) → Signature verification ✅
2. **Save comment** (Store API) → Signature verification ✅
3. **Query comments** (Store API) → Signature verification ✅

---

## ⚠️ SECURITY ISSUES FOUND

### 1. **No validation for `parentFileId`** 🔴 CRITICAL
**Issue:**
- Any user can comment on any `postFileId`
- No check that the post exists
- No check that the user has access to that post
- Comments can be created for non-existent posts (orphan comments)

**Impact:**
- Spam comments on non-existent posts
- Noise in the database
- Can be used for DoS attacks

**Solution:**
```csharp
// In CommentService.sendComment or PostController.GetComments
// Validate parentFileId exists and user has access
var parentPost = await _fileStorage.GetFileMetadataAsync(parentFileId, userPublicKey);
if (parentPost == null) {
    return BadRequest("Parent post not found or access denied");
}
```

---

### 2. **No rate limiting** 🟡 MEDIUM
**Issue:**
- No limit on how many comments a user can create
- Comments can be spammed for a DoS attack

**Impact:**
- Spam comments
- DoS attacks
- Wasted storage

**Solution:**
- Implement rate limiting middleware
- Limit: e.g. 10 comments/minute/user

---

### 3. **No validation for comment text length** 🟡 MEDIUM
**Issue:**
- No limit on comment text length
- Extremely long comments (megabytes) can be uploaded

**Impact:**
- DoS attacks (storage exhaustion)
- Performance issues

**Solution:**
```csharp
// In CommentService or FileController
const int MAX_COMMENT_LENGTH = 10000; // 10KB
if (commentText.Length > MAX_COMMENT_LENGTH) {
    return BadRequest("Comment text too long");
}
```

---

### 4. **Zero-Knowledge Leak: Store knows sharing relationships** 🟠 HIGH
**Issue:**
- For encrypted comments, the store sees `RecipientPubKey` in metadata
- The store can tell who has access to which comment
- Violates the zero-knowledge principle

**Example:**
```json
{
  "fileId": "comment_123",
  "type": "comment",
  "parentFileId": "post_456",
  "recipientPubKey": "024aa6c7f0bdd2fc4a9ccc4ce71b79e834457adae7ccc4b1fd61c9022c2a9da2d0",
  "ownerPubKey": "024aa6c7f0bdd2fc4a9ccc4ce71b79e834457adae7ccc4b1fd61c9022c2a9da2d0"
}
```
Store knows: User `024aa6c7...` has access to this comment.

**Impact:**
- Store can map sharing relationships
- Does not achieve zero-knowledge as designed

**Solution:**
- Use PRE (Proxy Re-Encryption) for comments as already done for posts
- Or accept the trade-off: zero-knowledge for comments is more complex than for posts

---

### 5. **Query comments leak metadata** 🟠 HIGH
**Issue:**
- When querying comments, a user can learn:
  - The number of comments (including encrypted ones they cannot access)
  - Comment metadata (FileId, OwnerPubKey, CreationTime)
  - They can enumerate all comments on a post

**Example:**
```csharp
// PostController.GetComments
var comments = await _fileStorage.ListCommentsByParentAsync(postFileId);
// Returns ALL comments, including encrypted ones the user cannot access
var accessibleComments = comments.Where(c => 
    c.IsPublic || 
    c.RecipientPubKey == userPubKey || 
    c.OwnerPubKey == userPubKey
);
```

**Impact:**
- User learns how many comments exist (including private ones)
- Comments can be enumerated to find patterns
- Metadata about comment authors is leaked

**Solution:**
- Return only comments the user is allowed to access
- Do not leak the total comment count
- Or implement pagination with cursor-based navigation

---

### 6. **No validation for `postOwnerPublicKey`** 🟡 MEDIUM
**Issue:**
- When creating an encrypted comment, the app sends `postOwnerPublicKey`
- The backend does not validate that `postOwnerPublicKey` is actually the post owner
- `postOwnerPublicKey` can be faked so the comment is shared with a different user

**Impact:**
- A comment can be shared with a user who is not the post owner
- Violates access control

**Solution:**
```csharp
// Validate postOwnerPublicKey matches actual post owner
var parentPost = await _fileStorage.GetFileMetadataAsync(parentFileId, userPublicKey);
if (parentPost == null || parentPost.OwnerPubKey != postOwnerPublicKey) {
    return BadRequest("Invalid post owner public key");
}
```

---

### 7. **Public comments stored as plaintext** 🟢 LOW (by design)
**Issue:**
- Public comments are stored as plaintext (unencrypted)
- The store can read the content

**Impact:**
- Store knows the content of public comments
- No privacy for public comments

**Solution:**
- This is by design: public comments = public
- Users need to understand clearly when they choose "public"
- A warning can be added in the UI

---

### 8. **No validation for signature timestamp** 🟡 MEDIUM
**Issue:**
- Signature verification has a timestamp window (5 minutes)
- Replay attacks are not checked
- Signatures can be reused within the window

**Impact:**
- Replay attacks within the 5-minute window

**Solution:**
- Implement a nonce or request ID to prevent replay
- Or reduce the timestamp window to 1 minute

---

### 9. **No access control when deleting a comment** 🟢 LOW
**Issue:**
- Only the comment owner can delete
- But there is no check that the comment belongs to a post the user is allowed to access

**Impact:**
- Users can delete their own comments, but this does not affect others

**Solution:**
- Currently OK: only the owner can delete
- Could add: only allow deleting your own comment on a post you have access to

---

## ✅ STRENGTHS

1. **Signature verification**: All requests have signature verification ✅
2. **Encryption**: Encrypted comments are encrypted with ECIES ✅
3. **Access control**: Filter comments based on IsPublic, RecipientPubKey, OwnerPubKey ✅
4. **Deduplication**: Duplicate metadata entries for encrypted comments are handled ✅

---

## 📋 PRIORITY RECOMMENDATIONS

### Priority 1 (Critical):
1. ✅ Validate `parentFileId` exists and user has access
2. ✅ Validate `postOwnerPublicKey` matches actual post owner

### Priority 2 (High):
3. ✅ Implement rate limiting
4. ✅ Validate comment text length
5. ✅ Fix query comments leak (return only accessible comments)

### Priority 3 (Medium):
6. ⚠️ Consider PRE for comments to achieve zero-knowledge
7. ⚠️ Implement nonce to prevent replay attacks

### Priority 4 (Low):
8. ⚠️ Add warning for public comments
9. ⚠️ Improve delete comment access control

---

## 🔍 TESTING CHECKLIST

- [ ] Test commenting on a non-existent post
- [ ] Test commenting on a post the user cannot access
- [ ] Test spam comments (rate limiting)
- [ ] Test overly long comment text
- [ ] Test fake postOwnerPublicKey
- [ ] Test querying comments on a post the user cannot access
- [ ] Test replay attack with signature
- [ ] Test deleting another user's comment
