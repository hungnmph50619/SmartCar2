(() => {
    if (!/^\/Bookings\/Details(?:\/|$)/i.test(window.location.pathname)) return;

    const normalize = value => (value ?? '').replace(/\s+/g, ' ').trim();
    const extensionHeading = Array.from(document.querySelectorAll('.card-header strong'))
        .find(item => normalize(item.textContent) === 'Gia hạn xe');
    const card = extensionHeading?.closest('.card');
    if (!card) return;

    const rows = Array.from(card.querySelectorAll('tbody tr'));
    const rejectedRows = rows.filter(row => normalize(row.textContent).includes('Đã từ chối'));
    if (rejectedRows.length === 0) return;

    const latestRejected = rejectedRows[rejectedRows.length - 1];
    const cells = latestRejected.querySelectorAll('td');
    const originalReturn = normalize(cells[0]?.textContent) || 'thời hạn trả xe theo hợp đồng';

    latestRejected.classList.add('table-danger');
    const statusCell = cells[3];
    if (statusCell) statusCell.innerHTML = '<span class="badge bg-danger">Đã từ chối</span>';

    const alert = document.createElement('div');
    alert.className = 'alert alert-danger border-danger mb-3';
    alert.innerHTML = `
        <div class="d-flex gap-2 align-items-start">
            <div class="fs-4" aria-hidden="true">⚠</div>
            <div>
                <div class="fw-bold">Yêu cầu gia hạn không được chấp thuận</div>
                <div class="mt-1">Thời hạn trả xe của bạn vẫn là <strong>${originalReturn}</strong>. Vui lòng trả xe đúng hạn.</div>
                <div class="small mt-2">Nếu tiếp tục giữ xe sau thời hạn, hệ thống sẽ ghi nhận xe quá hạn. Khi việc giữ xe làm khách có đơn kế tiếp không nhận được xe, SmartCar có thể xử lý vi phạm và bồi thường theo điều khoản đã ký.</div>
            </div>
        </div>`;

    const body = card.querySelector(':scope > .card-body');
    body?.prepend(alert);

    const form = card.querySelector('#customer-extension-form');
    if (form) {
        const notice = document.createElement('div');
        notice.className = 'alert alert-light border mb-3 small';
        notice.innerHTML = '<strong>Yêu cầu trước đã bị từ chối.</strong> Chỉ gửi yêu cầu mới khi có lý do hoặc tình huống mới cần SmartCar xem xét.';
        form.prepend(notice);
    }
})();
