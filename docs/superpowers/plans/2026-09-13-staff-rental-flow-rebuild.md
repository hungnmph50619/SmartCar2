# Staff rental flow rebuild implementation plan

> Execute directly on `hoangquangtruong_Staff`. Use TDD where a unit-testable rule exists. After each meaningful batch, verify the branch GitHub Actions run before continuing.

## Task 1 — Lock workflow rules with tests

**Files**
- Create: `tests/SmartCar.Tests/BookingWorkflowRulesTests.cs`
- Create: `src/SmartCar.Application/Features/Operations/BookingWorkflowRules.cs`
- Modify: `src/SmartCar.Infrastructure/Services/BookingOperationService.cs`

**Steps**
1. Add failing tests proving cancellation is allowed only before handover and terminal cancelled/no-show bookings remain Staff refund work when an open refund exists.
2. Push the red test and confirm CI fails for the missing rule.
3. Implement `BookingWorkflowRules`.
4. Use the rule in cancellation server validation and Staff work visibility.
5. Push and confirm CI is green.

## Task 2 — Remove conflicting payment/refund paths

**Files**
- Modify: `src/SmartCar.Application/Features/Payments/PaymentContracts.cs`
- Modify: `src/SmartCar.Infrastructure/Services/PaymentService.cs`
- Modify: `src/SmartCar.Web/Controllers/AdminPaymentsController.cs`

**Steps**
1. Remove `ConfirmRefundAsync` from the service contract and implementation so Admin cannot bypass refund approval.
2. Remove duplicate extension application from `AdminPaymentsController.ConfirmQr`; payment confirmation remains the atomic owner.
3. Remove automatic legacy financial repair from GET `Index` and normal approval requests.
4. Keep Admin refund action approval-only; Staff remains execution owner.
5. Build/test through CI.

## Task 3 — Make Staff review fresh at Admin approval

**Files**
- Modify: `src/SmartCar.Web/Controllers/AdminBookingsController.cs`

**Steps**
1. Keep audit history as trace only.
2. Inject `IBookingReviewService` and revalidate current KYC, vehicle, reservation and conflict state immediately before Admin approval.
3. Reject approval if current data no longer passes even when an old Staff audit exists.
4. Verify CI.

## Task 4 — Make handover/return identity writes atomic

**Files**
- Modify: `src/SmartCar.Application/Features/Handovers/HandoverContracts.cs`
- Modify: `src/SmartCar.Infrastructure/Services/HandoverService.cs`
- Modify: `src/SmartCar.Web/Controllers/HandoversController.cs`
- Modify: `src/SmartCar.Application/Features/Returns/ReturnContracts.cs`
- Modify: `src/SmartCar.Infrastructure/Services/ReturnService.cs`
- Modify: `src/SmartCar.Web/Controllers/ReturnsController.cs`

**Steps**
1. Pass verified Staff ID into service requests after controller-level original-document matching succeeds.
2. Persist identity verification fields in the same transaction that creates the handover/return record.
3. Delete the second controller-side `SaveChanges` identity write.
4. At signed handover verification, finalize actual `HandoverAt` to the real trip-start time before moving to `Rented`.
5. Verify CI.

## Task 5 — Make operational authorization explicit

**Files**
- Modify: `src/SmartCar.Web/Controllers/ReturnsController.cs`
- Modify: `src/SmartCar.Web/Controllers/AdminRentalDocumentsController.cs`
- Modify: `src/SmartCar.Web/Controllers/AdminSignedDocumentsController.cs`
- Modify/clean: `src/SmartCar.Web/Filters/StaffOperationsAuthorizationFilter.cs` and registration if no longer needed.

**Steps**
1. Mark operational controllers Staff-only directly.
2. Remove contradictory authorization indirection once all protected controllers are explicit.
3. Verify routes still work for Staff and are forbidden to Admin by attributes.
4. Verify CI.

## Task 6 — Rebuild counter rental as real search/autocomplete

**Files**
- Modify: `src/SmartCar.Web/ViewModels/StaffViewModels.cs`
- Modify: `src/SmartCar.Web/Controllers/StaffController.cs`
- Modify: `src/SmartCar.Web/Views/Staff/CounterRental.cshtml`
- Modify: `src/SmartCar.Web/wwwroot/css/staff-operations.css`

**Steps**
1. Remove preloaded `Customers`, `Vehicles`, and fake booking-time `PaymentMethod` fields.
2. Add server-side Customer autocomplete endpoint limited to active Customer-role accounts.
3. Add server-side Vehicle autocomplete endpoint using `IVehicleService.SearchAvailableAsync` and the selected time range.
4. Rebuild the View as hidden selected IDs + accessible search inputs + suggestion panels + selected summary cards.
5. Clear selected vehicle whenever the rental time changes.
6. Keep POST validation authoritative through `IBookingService.CreateAsync`.
7. Verify CI.

## Task 7 — Make post-approval payment choice functional

**Files**
- Modify: `src/SmartCar.Web/Controllers/StaffController.cs`
- Modify: `src/SmartCar.Web/Views/Staff/Details.cshtml`

**Steps**
1. Keep cash collection as a real Staff operation.
2. Add a Staff-at-counter QR/transfer submission action that calls `IPaymentService.SubmitQrPaymentAsync` for the booking customer and leaves it `AwaitingConfirmation` for Admin reconciliation.
3. Replace descriptive-only payment copy with two real action cards and accurate current payment status.
4. Verify CI.

## Task 8 — Rebuild Staff Details UX around tasks, not text

**Files**
- Modify: `src/SmartCar.Web/ViewModels/StaffViewModels.cs`
- Modify: `src/SmartCar.Web/Controllers/StaffController.cs`
- Modify: `src/SmartCar.Web/Views/Staff/Details.cshtml`
- Modify: `src/SmartCar.Web/wwwroot/css/staff-operations.css`

**Steps**
1. Show active work step prominently and compress completed steps.
2. Hide cancellation once any handover exists; show refund work for Cancelled/NoShow when applicable.
3. Display payment/refund states separately from booking lifecycle.
4. Keep document/identity evidence visible next to the action it gates.
5. Make destructive confirmations consequence-specific.
6. Verify desktop/mobile responsive CSS and CI.

## Task 9 — Final verification and audit

1. Confirm latest branch Actions run succeeds: build, unit tests, clean migration, no pending model changes.
2. Re-read changed controllers/services to ensure no duplicate owner remains for payment, refund, handover, return, or Staff review.
3. Search for stale `ConfirmRefundAsync`, automatic legacy repair calls, old counter dropdown collections, and duplicate extension `MarkPaidAsync` calls.
4. Report exact commits and any intentionally deferred schema cleanup (e.g. splitting `Payment.Method` channel/purpose) rather than claiming it is already done.
