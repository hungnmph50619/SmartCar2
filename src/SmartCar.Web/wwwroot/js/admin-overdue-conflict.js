(() => {
    const pathname = window.location.pathname.toLowerCase();
    if (!pathname.startsWith('/adminbookings/details')) return;
    const match = window.location.pathname.match(/\/AdminBookings\/Details\/(\d+)/i);
    const bookingId = match ? Number.parseInt(match[1], 10) : 0;
    if (!bookingId) return;

    const money = value => new Intl.NumberFormat('vi-VN').format(Number(value || 0)) + ' đ';
    const dateTime = value => {
        if (!value) return '-';
        const date = new Date(value);
        return Number.isNaN(date.getTime()) ? '-' : new Intl.DateTimeFormat('vi-VN', { day:'2-digit', month:'2-digit', year:'numeric', hour:'2-digit', minute:'2-digit', hour12:false }).format(date);
    };
    const findRentedCard = () => Array.from(document.querySelectorAll('h2')).find(h => h.textContent?.trim() === 'Đang thuê')?.closest('.card') || null;

    const render = data => {
        document.getElementById('live-overdue-conflict-card')?.remove();
        if (!data || data.state === 'none') return;
        const rentedCard = findRentedCard();
        if (!rentedCard) return;
        const card = document.createElement('div');
        card.id = 'live-overdue-conflict-card';
        card.className = `card card-body shadow-sm mb-4 ${data.state === 'affected' ? 'border-danger border-2' : 'border-warning border-2'}`;

        if (data.state === 'affected') {
            card.innerHTML = `
                <div class="d-flex justify-content-between align-items-start gap-3 flex-wrap">
                    <div><h2 class="h5 text-danger mb-1">Đơn #${bookingId} quá hạn – đang ảnh hưởng đơn #${data.affectedBookingId}</h2><div class="text-muted small">Gia hạn đã bị từ chối, xe vẫn chưa trả và đơn kế tiếp đã đến giờ nhận.</div></div>
                    <span class="badge bg-danger fs-6">Cần xử lý vi phạm</span>
                </div>
                <div class="row g-3 mt-2">
                    <div class="col-md-3"><div class="small text-muted">Hạn trả #${bookingId}</div><strong>${dateTime(data.returnDate)}</strong></div>
                    <div class="col-md-3"><div class="small text-muted">Đơn bị ảnh hưởng</div><strong>#${data.affectedBookingId}</strong></div>
                    <div class="col-md-3"><div class="small text-muted">Giờ nhận xe</div><strong>${dateTime(data.affectedPickupDate)}</strong></div>
                    <div class="col-md-3"><div class="small text-muted">Bồi thường phải chịu</div><strong class="text-danger">${money(data.affectedContractAmount)}</strong></div>
                </div>
                <div class="rounded border bg-light p-3 mt-3">
                    <div class="fw-bold mb-2">Dự kiến xử lý tiền của đơn #${bookingId}</div>
                    <div class="d-flex justify-content-between"><span>Cọc đang giữ</span><strong>${money(data.availableDeposit)}</strong></div>
                    <div class="d-flex justify-content-between"><span>Khấu trừ từ cọc</span><strong>${money(data.depositDeduction)}</strong></div>
                    <div class="d-flex justify-content-between border-top pt-2 mt-2"><span>Còn phải thu thêm từ khách #${bookingId}</span><strong class="${Number(data.outstandingAmount || 0) > 0 ? 'text-danger' : 'text-success'}">${money(data.outstandingAmount)}</strong></div>
                </div>
                <div class="alert alert-danger mt-3 mb-3"><strong>Cọc chưa được hoàn vì xe chưa trả.</strong> Hệ thống chỉ ghi khấu trừ sau khi Admin xác nhận. Nếu cọc không đủ, phần thiếu được tạo thành khoản phải thu thêm từ khách #${bookingId}.</div>
                <form action="/AdminOverdueViolations/Process" method="post" onsubmit="return confirm('Xác nhận đơn #${bookingId} đã bị từ chối gia hạn nhưng vẫn giữ xe quá hạn, làm ảnh hưởng đơn #${data.affectedBookingId}?');">
                    <input type="hidden" name="renterBookingId" value="${bookingId}" />
                    <input type="hidden" name="affectedBookingId" value="${data.affectedBookingId}" />
                    <input type="hidden" name="violationConfirmed" value="true" />
                    <div class="d-flex gap-2 flex-wrap"><a class="btn btn-outline-secondary" href="/AdminBookings/Details/${data.affectedBookingId}">Xem đơn #${data.affectedBookingId}</a><button class="btn btn-danger" type="submit">Xử lý vi phạm</button></div>
                </form>`;
            const token = document.querySelector('input[name="__RequestVerificationToken"]');
            if (token instanceof HTMLInputElement) {
                const hidden = document.createElement('input'); hidden.type='hidden'; hidden.name='__RequestVerificationToken'; hidden.value=token.value; card.querySelector('form')?.appendChild(hidden);
            }
        } else if (data.state === 'overdue-upcoming') {
            card.innerHTML = `<div class="d-flex justify-content-between gap-3 flex-wrap"><div><h2 class="h5 text-warning-emphasis mb-1">Quá hạn trả xe – có đơn kế tiếp</h2><div class="small text-muted">Đơn #${bookingId} đã quá hạn sau khi yêu cầu gia hạn bị từ chối.</div></div><span class="badge bg-warning text-dark fs-6">Theo dõi</span></div><div class="row g-3 mt-2"><div class="col-md-4"><div class="small text-muted">Hạn trả</div><strong>${dateTime(data.returnDate)}</strong></div><div class="col-md-4"><div class="small text-muted">Đơn kế tiếp</div><strong>#${data.affectedBookingId}</strong></div><div class="col-md-4"><div class="small text-muted">Giờ nhận xe</div><strong>${dateTime(data.affectedPickupDate)}</strong></div></div><div class="alert alert-warning mt-3 mb-0">Chưa đến giờ nhận của đơn #${data.affectedBookingId}: chưa bồi thường, chưa khấu trừ cọc. Cọc của #${bookingId} vẫn được giữ vì xe chưa trả.</div>`;
        } else {
            const rejectedText = data.rejectedExtension === false ? 'Xe đang quá hạn trả.' : 'Yêu cầu gia hạn đã bị từ chối và xe chưa được trả.';
            card.innerHTML = `<div class="d-flex justify-content-between gap-3 flex-wrap"><div><h2 class="h5 text-warning-emphasis mb-1">Đơn #${bookingId} đang quá hạn trả xe</h2><div class="small text-muted">${rejectedText}</div></div><span class="badge bg-warning text-dark fs-6">Quá hạn</span></div><div class="alert alert-warning mt-3 mb-0"><strong>Hạn trả:</strong> ${dateTime(data.returnDate)}. Cọc chưa được hoàn khi xe chưa trả. Hiện chưa có đơn kế tiếp đủ điều kiện xác định bị ảnh hưởng.</div>`;
        }
        rentedCard.insertAdjacentElement('afterend', card);
    };

    const refresh = async () => {
        try {
            const response = await fetch(`/AdminOverdueConflicts/Status?bookingId=${bookingId}&_=${Date.now()}`, { credentials:'same-origin', cache:'no-store', headers:{'X-Requested-With':'XMLHttpRequest'} });
            if (response.ok) render(await response.json());
        } catch { }
    };
    refresh();
})();