# SmartCar UX, Identity and Hold Hardening Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Remove duplicated Staff actions, make identity proof meaningful, harden booking holds, and rebuild the most error-prone Staff forms so UI constraints match backend rules.

**Architecture:** Keep existing BookingStatus values to avoid a risky state-machine migration; add structured review metadata to Booking. Store face evidence in secure storage with dedicated fields, not ImagePaths. Use camera-first capture and short-lived one-time capture sessions for cross-device handoff; Staff-only fallback upload is audited. Anti-hold abuse is enforced in policy-aware booking creation/expiry rather than only in UI.

**Tech Stack:** ASP.NET Core MVC .NET 8, EF Core 8, SQL Server, Identity, Razor, vanilla JavaScript, Bootstrap 5, existing GitHub Actions CI.

**Spec:** `docs/superpowers/specs/2026-09-13-staff-rental-flow-rebuild-design.md` plus decisions confirmed in chat on 2026-09-16.

## Global Constraints

- Staff handles operational rental work; Admin handles approval/oversight.
- Customer cannot freely upload identity face images; camera is the normal path.
- Staff may use manual face-file fallback only with reason + audit when camera/handoff cannot be used.
- Face images are private secure documents, not public wwwroot evidence images.
- Customer account/booking holder must be the person who receives and returns the vehicle.
- Never rely on filename/text-only UX for vehicle evidence images.
- UI restrictions supplement, never replace, server-side validation.
- Keep build, tests, clean migration and EF pending-model CI green before fast-forwarding the target branch.

---

### Task 1: Structured Staff review state

**Files:**
- Modify: `src/SmartCar.Domain/Entities/Booking.cs`
- Modify: `src/SmartCar.Infrastructure/Persistence/ApplicationDbContext.cs`
- Modify: `src/SmartCar.Web/Controllers/StaffBookingOperationsController.cs`
- Modify: `src/SmartCar.Web/Controllers/AdminBookingsController.cs`
- Modify: `src/SmartCar.Web/Controllers/StaffController.cs`
- Modify: `src/SmartCar.Web/ViewModels/StaffViewModels.cs`
- Modify: `src/SmartCar.Web/Views/Staff/Bookings.cshtml`
- Modify: `src/SmartCar.Web/Views/Staff/Details.cshtml`
- Test: `tests/SmartCar.Tests/StaffWorkflowRegressionTests.cs`
- Create migration + update model snapshot.

**Interfaces:**
- Produces `Booking.StaffReviewedAt` and `Booking.StaffReviewedByStaffId`.
- Staff review POST is idempotent and no longer remains visible once recorded.

- [ ] Write failing tests proving a reviewed PendingConfirmation booking is represented as reviewed and Admin cannot approve without structured review metadata.
- [ ] Add fields/mapping/migration.
- [ ] Persist review metadata in `ReviewForApproval` after live validation.
- [ ] Remove list-page Review action; list only opens the task.
- [ ] Details shows Review only when `StaffReviewedAt == null`; otherwise shows waiting-for-Admin state.
- [ ] Admin requires structured review metadata and still performs live revalidation.
- [ ] Run CI.

### Task 2: Payment QR consistency

**Files:**
- Modify: `src/SmartCar.Web/Controllers/StaffController.cs`
- Modify: `src/SmartCar.Web/Views/Staff/Details.cshtml`
- Reuse: `PaymentQr:*` configuration used by `BookingsController`.

- [ ] Add regression test/helper test for configured QR metadata if practical.
- [ ] Staff Details reads BankName, AccountNumber, AccountHolder and QrImagePath from configuration.
- [ ] Render QR with `object-fit: contain`, no forced crop, larger scan-safe display, and bank/account/transfer-content text.
- [ ] Keep SubmitCounterQr as state-changing action, not a visual-only button.

### Task 3: Face identity evidence data model

**Files:**
- Modify: `src/SmartCar.Infrastructure/Identity/ApplicationUser.cs`
- Modify: `src/SmartCar.Domain/Entities/VehicleHandover.cs`
- Modify: `src/SmartCar.Domain/Entities/VehicleReturn.cs`
- Create: `src/SmartCar.Domain/Entities/IdentityCaptureSession.cs`
- Modify: `src/SmartCar.Infrastructure/Persistence/ApplicationDbContext.cs`
- Create migration + update model snapshot.

**Fields:**
- Customer: `IdentityFaceImagePath`, `IdentityFaceCapturedAt`, `IdentityFaceCaptureMethod`.
- Handover: `ReceiverFaceImagePath`, `ReceiverFaceCapturedAt`, `ReceiverFaceCaptureMethod`.
- Return: `ReturnerFaceImagePath`, `ReturnerFaceCapturedAt`, `ReturnerFaceCaptureMethod`.
- Session: token hash, purpose, customer/booking target, creator, expiry, completion, secure image path, capture method.

- [ ] Write entity/policy tests for expiry and one-time completion.
- [ ] Add model + mapping + migration.
- [ ] Ensure all face paths are stored through `ISecureDocumentStorage`.

### Task 4: Camera-first identity capture with cross-device handoff

**Files:**
- Create: `src/SmartCar.Web/Controllers/IdentityCaptureController.cs`
- Create: `src/SmartCar.Web/ViewModels/IdentityCaptureViewModels.cs`
- Create: `src/SmartCar.Web/Views/IdentityCapture/Capture.cshtml`
- Create: `src/SmartCar.Web/wwwroot/js/identity-capture.js`
- Modify: `src/SmartCar.Web/SmartCar.Web.csproj` only if a local QR encoder package is required.

**Rules:**
- Customer cannot browse/upload a face file in normal KYC flow.
- Main device can capture via `getUserMedia`.
- If no camera, create 5–10 minute one-time token for mobile capture; mobile page only has permission to submit that exact capture target.
- Staff manual fallback upload is authorized to Staff only, requires reason, and writes audit.

- [ ] Tests for token expiry, one-time use, target binding and Staff-only fallback.
- [ ] Implement secure session creation/status/capture endpoints.
- [ ] Implement camera UI and mobile handoff URL/QR.
- [ ] Implement Staff fallback with audit.

### Task 5: KYC face proof

**Files:**
- Modify: `src/SmartCar.Web/ViewModels/KycPackageSubmitViewModel.cs`
- Modify: `src/SmartCar.Web/Controllers/KycPackageController.cs`
- Modify: `src/SmartCar.Web/Views/Profile/_InitialDocumentsVerificationForm.cshtml`
- Modify: `src/SmartCar.Web/wwwroot/js/profile-interactions.js`
- Modify Admin KYC review view/controller to display face beside CCCD.

- [ ] Require completed camera capture before initial KYC submit.
- [ ] Bind the completed session to the logged-in Customer and reject other-owner/reused/expired sessions.
- [ ] Move completed secure image into `ApplicationUser.IdentityFaceImagePath` as part of KYC transaction.
- [ ] Admin review displays KYC face next to document images.

### Task 6: Handover/return identity UX

**Files:**
- Modify: `src/SmartCar.Web/ViewModels/RentalFlowViewModels.cs`
- Modify: `src/SmartCar.Web/Controllers/HandoversController.cs`
- Modify: `src/SmartCar.Web/Controllers/ReturnsController.cs`
- Modify: `src/SmartCar.Infrastructure/Services/HandoverService.cs`
- Modify: `src/SmartCar.Infrastructure/Services/ReturnService.cs`
- Modify: `src/SmartCar.Web/Views/Handovers/Create.cshtml`
- Modify: `src/SmartCar.Web/Views/Returns/Create.cshtml`

- [ ] Remove full CCCD/GPLX re-entry from Staff handover.
- [ ] Show KYC face + masked document numbers + validity data.
- [ ] Require original-document checkboxes and completed receiver face capture.
- [ ] Require return face capture and show KYC + receiver face for comparison.
- [ ] Persist face path/time/method in same transaction as handover/return.

### Task 7: Handover/return time correctness

- [ ] Remove editable `HandoverAt` from draft form; actual handover time is server `DateTime.Now` only when signed handover is verified and trip starts.
- [ ] Draft handover can be prepared before pickup only if it does not start the trip; start-trip action enforces `now >= PickupDate` and `now < ReturnDate`.
- [ ] Return form defaults to now and may accept correction only within a small bounded past window; future values are blocked client + server.
- [ ] Add regression tests for before-pickup, after-return, and future-return boundaries.

### Task 8: Hard numeric inputs across operational forms

**Files:**
- Create/modify shared JS in `wwwroot/js/site.js` or a focused numeric-input module.
- Update Handover, Return, HandoverEdit, ReturnEdit, vehicle, maintenance, incidents, charges and settings numeric fields.

- [ ] Add `data-numeric-min/max/integer/max-digits` behavior that sanitizes on input/paste rather than only reporting invalidity.
- [ ] Fuel physically clamps to 0..100 while typing/pasting.
- [ ] Mileage accepts digits only and bounded max digits/value; return mileage cannot drop below handover mileage.
- [ ] Money/year/count fields receive appropriate hard bounds while server validation remains authoritative.

### Task 9: Vehicle evidence image UX + retry persistence

**Files:**
- Modify Handover/Return create views/controllers.
- Create temporary evidence-upload session entity/service or reuse a draft-token store with cleanup.
- Reuse `ImageFileValidator` duplicate-content checks.

- [ ] Seven required evidence slots each show labeled thumbnail, replace/remove control, expected example text and server-side label binding.
- [ ] Additional images are truly optional.
- [ ] Reject identical-content images reused across required slots.
- [ ] If non-file form validation fails, keep uploaded images in a short-lived server draft and re-render thumbnails so Staff does not reselect everything.
- [ ] Delete draft files after success/expiry/cancel.
- [ ] Return form no longer assigns first seven arbitrary files silently to semantic positions.

### Task 10: Anti-hold abuse

**Files:**
- Modify `BookingReservationPolicy` and policy-aware booking creation/cleanup.
- Add booking/customer hold abuse metadata or an explicit HoldAttempt ledger.
- Add tests.

**Rules:**
- At most one active unpaid hold per Customer.
- PendingConfirmation and PendingPayment keep existing TTLs.
- AwaitingConfirmation receives a finite reconciliation TTL instead of indefinite blocking.
- Timeout #1 in rolling 24h: same vehicle cooldown 30m.
- Timeout #2: same vehicle cooldown 2h.
- Timeout #3: block creating new bookings for 24h.
- Paid bookings do not count; Staff/Admin can see the reason and Admin can override/unblock with audit.

- [ ] Write failing policy tests.
- [ ] Implement ledger/fields and migration.
- [ ] Enforce in create and cleanup.
- [ ] Surface clear UX messages and remaining cooldown time.

### Task 11: Full UX/state audit and verification

- [ ] Search all Razor numeric/file/datetime inputs and apply shared rules consistently.
- [ ] Search Staff/Admin/Customer views for buttons whose action does not change persisted state.
- [ ] Search duplicate workflow actions exposed on list + detail pages.
- [ ] Verify authorization attributes match runtime role ownership.
- [ ] Verify all image screens render thumbnails and semantic labels instead of raw filenames only.
- [ ] Run GitHub Actions: Release build, unit tests, migrate clean SQL Server, EF pending model changes.
- [ ] Fast-forward `hoangquangtruong_Staff` only after green CI.
