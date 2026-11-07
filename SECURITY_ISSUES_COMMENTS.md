# 🔒 Security Analysis: Comment Flow

## Flow Overview
1. **Đăng comment** (Flutter app) → Signature verification ✅
2. **Lưu comment** (Store API) → Signature verification ✅
3. **Query comments** (Store API) → Signature verification ✅

---

## ⚠️ VẤN ĐỀ BẢO MẬT PHÁT HIỆN

### 1. **Không có validation cho `parentFileId`** 🔴 CRITICAL
**Vấn đề:**
- Bất kỳ user nào cũng có thể comment vào bất kỳ `postFileId` nào
- Không kiểm tra xem post có tồn tại không
- Không kiểm tra xem user có quyền truy cập post đó không
- Có thể tạo comment cho post không tồn tại (tạo orphan comments)

**Tác động:**
- Spam comments vào post không tồn tại
- Tạo noise trong database
- Có thể dùng để tấn công DoS

**Giải pháp:**
```csharp
// Trong CommentService.sendComment hoặc PostController.GetComments
// Validate parentFileId exists and user has access
var parentPost = await _fileStorage.GetFileMetadataAsync(parentFileId, userPublicKey);
if (parentPost == null) {
    return BadRequest("Parent post not found or access denied");
}
```

---

### 2. **Không có rate limiting** 🟡 MEDIUM
**Vấn đề:**
- Không giới hạn số lượng comments một user có thể tạo
- Có thể spam comments để tấn công DoS

**Tác động:**
- Spam comments
- Tấn công DoS
- Lãng phí storage

**Giải pháp:**
- Implement rate limiting middleware
- Giới hạn: ví dụ 10 comments/phút/user

---

### 3. **Không có validation cho comment text length** 🟡 MEDIUM
**Vấn đề:**
- Không giới hạn độ dài comment text
- Có thể upload comment rất dài (hàng MB)

**Tác động:**
- Tấn công DoS (storage exhaustion)
- Performance issues

**Giải pháp:**
```csharp
// Trong CommentService hoặc FileController
const int MAX_COMMENT_LENGTH = 10000; // 10KB
if (commentText.Length > MAX_COMMENT_LENGTH) {
    return BadRequest("Comment text too long");
}
```

---

### 4. **Zero-Knowledge Leak: Store biết sharing relationships** 🟠 HIGH
**Vấn đề:**
- Với encrypted comments, store thấy `RecipientPubKey` trong metadata
- Store biết được ai có quyền truy cập comment nào
- Vi phạm nguyên tắc zero-knowledge

**Ví dụ:**
```json
{
  "fileId": "comment_123",
  "type": "comment",
  "parentFileId": "post_456",
  "recipientPubKey": "024aa6c7f0bdd2fc4a9ccc4ce71b79e834457adae7ccc4b1fd61c9022c2a9da2d0",
  "ownerPubKey": "024aa6c7f0bdd2fc4a9ccc4ce71b79e834457adae7ccc4b1fd61c9022c2a9da2d0"
}
```
Store biết: User `024aa6c7...` có quyền truy cập comment này.

**Tác động:**
- Store có thể map sharing relationships
- Không đạt zero-knowledge như thiết kế

**Giải pháp:**
- Sử dụng PRE (Proxy Re-Encryption) cho comments như đã làm với posts
- Hoặc chấp nhận trade-off: zero-knowledge cho comments phức tạp hơn posts

---

### 5. **Query comments leak metadata** 🟠 HIGH
**Vấn đề:**
- Khi query comments, user có thể biết được:
  - Số lượng comments (kể cả encrypted mà họ không có quyền truy cập)
  - Metadata của comments (FileId, OwnerPubKey, CreationTime)
  - Có thể enumerate tất cả comments của một post

**Ví dụ:**
```csharp
// PostController.GetComments
var comments = await _fileStorage.ListCommentsByParentAsync(postFileId);
// Trả về TẤT CẢ comments, kể cả encrypted mà user không có quyền
var accessibleComments = comments.Where(c => 
    c.IsPublic || 
    c.RecipientPubKey == userPubKey || 
    c.OwnerPubKey == userPubKey
);
```

**Tác động:**
- User biết được có bao nhiêu comments (kể cả private)
- Có thể enumerate comments để tìm patterns
- Leak metadata về comment authors

**Giải pháp:**
- Chỉ trả về comments mà user có quyền truy cập
- Không leak số lượng total comments
- Hoặc implement pagination với cursor-based navigation

---

### 6. **Không có validation cho `postOwnerPublicKey`** 🟡 MEDIUM
**Vấn đề:**
- Khi tạo encrypted comment, app gửi `postOwnerPublicKey`
- Backend không validate xem `postOwnerPublicKey` có đúng là owner của post không
- Có thể fake `postOwnerPublicKey` để comment được chia sẻ với user khác

**Tác động:**
- Có thể chia sẻ comment với user không phải post owner
- Vi phạm access control

**Giải pháp:**
```csharp
// Validate postOwnerPublicKey matches actual post owner
var parentPost = await _fileStorage.GetFileMetadataAsync(parentFileId, userPublicKey);
if (parentPost == null || parentPost.OwnerPubKey != postOwnerPublicKey) {
    return BadRequest("Invalid post owner public key");
}
```

---

### 7. **Public comments lưu plaintext** 🟢 LOW (by design)
**Vấn đề:**
- Public comments được lưu plaintext (không mã hóa)
- Store có thể đọc được nội dung

**Tác động:**
- Store biết nội dung public comments
- Không có privacy cho public comments

**Giải pháp:**
- Đây là by design: public comments = public
- User cần hiểu rõ khi chọn "public"
- Có thể thêm warning trong UI

---

### 8. **Không có validation cho signature timestamp** 🟡 MEDIUM
**Vấn đề:**
- Signature verification có timestamp window (5 phút)
- Nhưng không check replay attacks
- Có thể reuse signature trong window

**Tác động:**
- Replay attacks trong 5 phút window

**Giải pháp:**
- Implement nonce hoặc request ID để prevent replay
- Hoặc giảm timestamp window xuống 1 phút

---

### 9. **Không có access control khi delete comment** 🟢 LOW
**Vấn đề:**
- Chỉ comment owner mới có thể delete
- Nhưng không check xem comment có thuộc về post mà user có quyền không

**Tác động:**
- Có thể delete comment của chính mình, nhưng không ảnh hưởng đến người khác

**Giải pháp:**
- Hiện tại đã OK: chỉ owner mới delete được
- Có thể thêm: chỉ delete được comment của mình trên post mà mình có quyền

---

## ✅ ĐIỂM TỐT

1. **Signature verification**: Tất cả requests đều có signature verification ✅
2. **Encryption**: Encrypted comments được mã hóa bằng ECIES ✅
3. **Access control**: Filter comments dựa trên IsPublic, RecipientPubKey, OwnerPubKey ✅
4. **Deduplication**: Xử lý duplicate metadata entries cho encrypted comments ✅

---

## 📋 KHUYẾN NGHỊ ƯU TIÊN

### Priority 1 (Critical):
1. ✅ Validate `parentFileId` exists and user has access
2. ✅ Validate `postOwnerPublicKey` matches actual post owner

### Priority 2 (High):
3. ✅ Implement rate limiting
4. ✅ Validate comment text length
5. ✅ Fix query comments leak (chỉ trả về accessible comments)

### Priority 3 (Medium):
6. ⚠️ Consider PRE for comments để đạt zero-knowledge
7. ⚠️ Implement nonce để prevent replay attacks

### Priority 4 (Low):
8. ⚠️ Add warning cho public comments
9. ⚠️ Improve delete comment access control

---

## 🔍 TESTING CHECKLIST

- [ ] Test comment vào post không tồn tại
- [ ] Test comment vào post mà user không có quyền
- [ ] Test spam comments (rate limiting)
- [ ] Test comment text quá dài
- [ ] Test fake postOwnerPublicKey
- [ ] Test query comments của post mà user không có quyền
- [ ] Test replay attack với signature
- [ ] Test delete comment của user khác




