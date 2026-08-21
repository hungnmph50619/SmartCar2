(() => {
    const pathname = window.location.pathname.toLowerCase();
    if (!pathname.startsWith('/adminbookings/details')) return;

    const bookingId = pathname.split('/').filter(Boolean).pop();
    if (!bookingId || !/^\d+$/.test(bookingId)) return;

    const money = value => new Intl.NumberFormat('vi-VN').format(Number(value || 0)) + ' đ';
    const dateTime = value => {
        if (!value) return '-';
        const date = new Date(value);
        return Number.isNaN(date.getTime()) ? value : date.toLocaleString('vi-VN', { hour12: false });
    };

    const findRentedCard = () => {
        const heading = Array.from(document.querySelectorAll('h2')).find(h => h.textContent?.trim() === 'Đang thuê');
        return heading?.closest('.card') || null;
    };

    const render = data => {
        document.getElementById('live-overdue-conflict-card')?.remove();
        if (!data || data.state === 'none') return;

        const rentedCard = findRentedCard();
        if (!rentedCard) return;

        const card = document.createElement('div');
        card.id = 'live-overdue-conflict-card';
        card.className = `card card-body shadow-sm mb-4 ${data.state === 'affected' ? 'border-danger' : 'border-warning'}`;

        if (data.state === 'overdue') {
            card.innerHTML = `
                <div class="d-flex justify-content-between align-items-start gap-3 flex-wrap">
                    <div><h2 class="h5 text-warning mb-1">Quá hạn trả xe</h2><div class="small text-muted">Yêu cầu gia hạn đã bị từ chối và đơn #${data.renterBookingId} vẫn chưa hoàn tất trả xe.</div></div>
                    <span class="badge bg-warning text-dark">Theo dõi quá hạn</span>
                </div>
                <div class="alert alert-warning mt-3 mb-0"><strong>Hạn trả:</strong> ${dateTime(data.returnDate)}. Hiện chưa xác định đơn kế tiếp đã bị ảnh hưởng, vì vậy chưa ghi nhận bồi thường.</div>`;
        } else if (data.state === 'overdue-upcoming') {
            card.innerHTML = `
                <div class="d-flex justify-content-between align-items-start gap-3 flex-wrap">
                    <div><h2 class="h5 text-warning mb-1">Quá hạn trả xe – có đơn kế tiếp</h2><div class="small text-muted">Đơn #${data.renterBookingId} đã quá hạn sau khi bị từ chối gia hạn.</div></div>
                    <span class="badge bg-warning text-dark">Cần theo dõi</span>
                </div>
                <div class="row g-3 mt-1">
                    <div class="col-md-4"><div class="small text-muted">Hạn trả</div><strong>${dateTime(data.returnDate)}</strong></div>
                    <div class="col-md-4"><div class="small text-muted">Đơn kế tiếp</div><strong>#${data.affectedBookingId}</strong></div>
                    <div class="col-md-4"><div class="small text-muted">Giờ khách kế tiếp nhận xe</div><strong>${dateTime(data.affectedPickupDate)}</strong></div>
                </div>
                <div class="alert alert-warning mt-3 mb-0">Xe đang quá hạn nhưng <strong>chưa đến giờ nhận của đơn #${data.affectedBookingId}</strong>. Chưa được xác nhận vi phạm gây thiệt hại cho đơn kế tiếp.</div>`;
        } else {
            card.innerHTML = `
                <div class="d-flex justify-content-between align-items-start gap-3 flex-wrap">
                    <div><h2 class="h5 text-danger mb-1">Đơn #${data.renterBookingId} quá hạn – đang ảnh hưởng đơn #${data.affectedBookingId}</h2><div class="small text-muted">Yêu cầu gia hạn đã bị từ chối, xe chưa được trả và đơn kế tiếp đã đến giờ nhận.</div></div>
                    <span class="badge bg-danger">Cần xử lý vi phạm</span>
                </div>
                <div class="row g-3 mt-1">
                    <div class="col-md-3"><div class="small text-muted">Hạn trả #${data.renterBookingId}</div><strong>${dateTime(data.returnDate)}</strong></div>
                    <div class="col-md-3"><div class="small text-muted">Đơn bị ảnh hưởng</div><strong>#${data.affectedBookingId}</strong></div>
                    <div class="col-md-3"><div class="small text-muted">Giờ nhận xe</div><strong>${dateTime(data.affectedPickupDate)}</strong></div>
                    <div class="col-md-3"><div class="small text-muted">Giá hợp đồng bị ảnh hưởng</div><strong class="text-danger">${money(data.affectedContractAmount)}</strong></div>
                    <div class="col-md-6"><div class="small text-muted">Cọc còn khả dụng của đơn #${data.renterBookingId}</div><strong>${money(data.availableDeposit)}</strong></div>
                    <div class="col-md-6"><div class="small text-muted">Căn cứ hệ thống</div><span class="small">Gia hạn đã bị từ chối · đã quá hạn · đơn kế tiếp đã đến giờ nhận.</span></div>
                </div>
                <div class="alert alert-danger mt-3 mb-3"><strong>Không tự động khấu trừ cọc.</strong> Quản trị viên phải xác nhận khách đã được thông báo từ chối nhưng vẫn cố tình giữ xe làm ảnh hưởng đơn kế tiếp.</div>
                <form action="/AdminExtensionCompensations/RecordIntentionalConflictCompensation" method="post" data-confirm="Xác nhận đơn #${data.renterBookingId} vi phạm và làm ảnh hưởng đơn #${data.affectedBookingId}?">
                    <input type="hidden" name="renterBookingId" value="${data.renterBookingId}" />
                    <input type="hidden" name="affectedBookingId" value="${data.affectedBookingId}" />
                    <input type="hidden" name="violationConfirmed" value="true" />
                    <div class="d-flex gap-2 flex-wrap">
                        <a class="btn btn-outline-secondary" href="/AdminBookings/Details/${data.affectedBookingId}">Xem đơn #${data.affectedBookingId}</a>
                        <button class="btn btn-danger" type="submit">Xử lý vi phạm</button>
                    </div>
                </form>`;

            const token = document.querySelector('input[name="__RequestVerificationToken"]');
            if (token instanceof HTMLInputElement) {
                const hidden = document.createElement('input');
                hidden.type = 'hidden';
                hidden.name = '__RequestVerificationToken';
                hidden.value = token.value;
                card.querySelector('form')?.appendChild(hidden);
            }
        }

        rentedCard.insertAdjacentElement('afterend', card);
    };

    const refresh = async () => {
        try {
            const response = await fetch(`/AdminOverdueConflicts/Status?bookingId=${encodeURIComponent(bookingId)}`, {
                headers: { 'X-Requested-With': 'XMLHttpRequest' },
                cache: 'no-store'
            });
            if (!response.ok) return;
            render(await response.json());
        } catch {
            // Trang chi tiết vẫn hoạt động bình thường nếu kiểm tra cảnh báo tạm thời thất bại.
        }
    };

    refresh();
    window.setInterval(refresh, 60000);
})();
