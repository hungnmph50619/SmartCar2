# Staff rental flow rebuild design

## Goal

Rebuild the Staff rental workflow so every visible action corresponds to real backend behavior, with one clear owner for each business transition. UI text must describe existing functionality rather than stand in for missing functionality.

## Workflow ownership

- Customer: creates bookings, can cancel their own booking before handover, can submit QR/bank payment confirmation.
- Staff: reviews booking eligibility, creates counter bookings, receives cash, prepares vehicles, verifies identity, creates handover/return records, verifies signed records, inspects returns, records charges, executes approved refunds.
- Admin: approves/rejects bookings after Staff review, reconciles transfer payments, approves/rejects refund requests, handles governance-level decisions such as exceptional extension conflicts.

## State principles

1. `BookingStatus` describes the rental lifecycle, not money-transfer progress.
2. `PaymentStatus` is the source of truth for payment/refund progress.
3. A cancelled or no-show booking can still have an open refund; Staff must still be able to access it through refund work queues.
4. Cancellation is forbidden once a handover record exists, even if the signed handover has not yet been verified.
5. Staff review must be revalidated from current booking/customer/vehicle data when Admin approves; an old audit entry alone is never sufficient.

## Payment and refund rules

- Remove the legacy Admin direct-refund operation. Admin may only approve refund lines (`AwaitingRefund -> RefundApproved`).
- Staff executes approved refund lines (`RefundApproved -> Refunded`) and records the real transaction code.
- GET endpoints must be read-only. Legacy financial repair code must not execute when opening a page.
- QR extension confirmation has a single owner: `PaymentService` applies the paid extension atomically with payment confirmation; controller code must not apply it a second time.
- The current `Payment.Method` schema is retained in the first safe pass to avoid a risky migration, but UI must not mislabel refund purpose strings as payment channels. A later schema migration can split channel from refund/adjustment purpose.

## Handover and return integrity

- Creating the electronic handover and marking the in-person identity check must commit atomically in `HandoverService`.
- Creating the return record and marking the returning-customer identity check must commit atomically in `ReturnService`.
- The actual handover time is finalized when the signed handover is verified and the booking moves to `Rented`.
- Operational document controllers are Staff-only in their controller authorization, not indirectly through a global filter that contradicts attributes.

## Counter rental UX

- Remove full customer and vehicle dropdowns.
- Customer selection becomes server-side autocomplete by name, phone, or email.
- Vehicle selection becomes server-side autocomplete after pickup/return times are valid and uses the same policy-aware vehicle availability service as online booking.
- Changing pickup/return time invalidates a previously selected vehicle and requires a fresh availability lookup.
- The booking form does not pretend payment is chosen at booking creation. Payment is selected only after Admin approval.
- Pending-payment Staff UI exposes real actions: receive cash or record that the customer has submitted a QR/bank transfer for Admin reconciliation.

## UI/UX direction

- Staff Details becomes a task-oriented timeline: current step is prominent, completed steps are compact, impossible actions are hidden rather than explained as if they existed.
- Destructive actions show business consequences before submission.
- Refund state is displayed independently from rental state.
- Search/autocomplete components show loading, empty, selected, and invalidated states.
- Mobile layouts remain usable; controls have explicit labels and keyboard-friendly behavior.

## Verification

Every production change is gated by the existing GitHub Actions workflow on `hoangquangtruong_Staff`, which builds Release, runs tests, applies migrations to a clean SQL Server database, and checks for pending EF model changes.
