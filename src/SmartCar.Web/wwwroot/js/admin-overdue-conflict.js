(() => {
    const pathname = window.location.pathname.toLowerCase();
    if (!pathname.startsWith('/adminbookings/details')) return;

    const data = document.getElementById('overdue-conflict-data');
    if (!(data instanceof HTMLElement)) return;

    const renterId = data.dataset.renterId;
    const affectedId = data.dataset.affectedId;
    const affectedPickup = data.dataset.affectedPickup;
    const contractAmount = data.dataset.contractAmount;
    const availableDeposit = data.dataset.availableDeposit;
    const affectedCustomer = data.dataset.affectedCustomer || 'khách có đơn kế tiếp';
    if (!renterId || !affectedId) return;

    const rentedHeading = Array.from(document.querySelectorAll('h2')).find(h => h.textContent?.trim() === 'Đang thuê');
    const rentedCard = rentedHeading?.closest('.card');
    if (!rentedCard) return;

    const card = document.createElement('div');
    card.className = 'card card-body shadow-sm mb-4 border-danger';
    card.innerHTML = `
        <div class="d-flex justify-content-between align-items-start gap-3 flex-wrap">
            <div>
                <h2 class="h5 text-danger mb-1">Quá hạn trả xe – đang ảnh hưởng đơn kế tiếp</h2>
                <div class="text-muted small">Yêu cầu gia hạn thông thường của đơn #${renterId} đã bị từ chối nhưng xe vẫn chưa được trả.</div>
            </div>
            <span class="badge bg-danger">Cần xử lý</span>
        </div>
        <div class="row g-3 mt-1">
            <div class="col-md-4"><div class="small text-muted">Đơn bị ảnh hưởng</div><strong>#${affectedId}</strong><div class="small">${affectedCustomer}</div></div>
            <div class="col-md-4"><div class="small text-muted">Giờ nhận xe của đơn kế tiếp</div><strong>${affectedPickup || '-'}</strong></div>
            <div class="col-md-4"><div class="small text-muted">Giá hợp đồng đơn bị ảnh hưởng</div><strong class="text-danger">${contractAmount || '0 đ'}</strong></div>
            <div class="col-md-6"><div class="small text-muted">Cọc còn khả dụng của đơn đang thuê</div><strong>${availableDeposit || '0 đ'}</strong></div>
            <div class="col-md-6"><div class="small text-muted">Căn cứ</div><span class="small">Đã từ chối gia hạn · đã quá giờ trả · đơn kế tiếp đã đến giờ nhận.</span></div>
        </div>
        <div class="alert alert-light border mt-3 mb-3 small">
            Theo biên bản giao xe đã ký, nếu khách đã được từ chối gia hạn nhưng vẫn cố tình không trả xe đúng hạn làm ảnh hưởng đơn kế tiếp, khoản bồi thường bằng giá hợp đồng của đơn bị ảnh hưởng và được khấu trừ từ tiền cọc.
        </div>
        <form action="/AdminExtensionCompensations/RecordIntentionalConflictCompensation" method="post" data-confirm="Xác nhận khách đã được thông báo từ chối gia hạn nhưng vẫn cố tình giữ xe, làm đơn #${affectedId} không nhận được xe?">
            <input type="hidden" name="renterBookingId" value="${renterId}" />
            <input type="hidden" name="affectedBookingId" value="${affectedId}" />
            <input type="hidden" name="violationConfirmed" value="true" />
            <input type="hidden" name="__RequestVerificationToken" value="${document.querySelector('input[name="__RequestVerificationToken"]')?.value || ''}" />
            <div class="d-flex gap-2 flex-wrap">
                <a class="btn btn-outline-secondary" href="/AdminBookings/Details/${affectedId}">Xem đơn kế tiếp</a>
                <button class="btn btn-danger" type="submit">Xác nhận vi phạm & ghi nhận bồi thường</button>
            </div>
        </form>`;
    rentedCard.insertAdjacentElement('afterend', card);
})();
