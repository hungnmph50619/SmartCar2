const assert = require('node:assert/strict');
const { readFileSync } = require('node:fs');
const { join } = require('node:path');
const { runInNewContext } = require('node:vm');
const { test } = require('node:test');

const script = readFileSync(join(__dirname, '../../src/SmartCar.Web/wwwroot/js/site.js'), 'utf8');

// Only the DOM surface is substituted; submit cancellation uses native EventTarget/Event.
function loadingForm() {
    class Button {
        constructor() {
            this.dataset = {};
            this.disabled = false;
            this.innerHTML = 'Lưu';
            this.attributes = new Map();
        }
        setAttribute(name, value) { this.attributes.set(name, value); }
        removeAttribute(name) { this.attributes.delete(name); }
    }
    const buttons = [new Button(), new Button()];
    const form = Object.assign(new EventTarget(), {
        dataset: {}, isConnected: true,
        checkValidity: () => true,
        hasAttribute: name => name === 'data-loading-form',
        querySelector: () => buttons[0]
    });
    const window = Object.assign(new EventTarget(), {
        setTimeout: fn => timers.push(fn), confirm: () => true
    });
    const timers = [];
    const document = {
        addEventListener() {},
        querySelectorAll: selector => selector.includes('aria-busy')
            ? buttons.filter(button => button.attributes.get('aria-busy') === 'true') : [form],
        createElement: () => ({ set textContent(value) { this.innerHTML = value; } })
    };
    runInNewContext(script + '\ninitializeForms();', {
        window, document, HTMLButtonElement: Button, HTMLInputElement: class {}
    });
    return {
        form, buttons,
        submit(index = 0) {
            const event = new Event('submit', { cancelable: true });
            Object.defineProperty(event, 'submitter', { value: buttons[index] });
            form.dispatchEvent(event);
            return event;
        },
        flush() { while (timers.length) timers.shift()(); },
        restore() { window.dispatchEvent(new Event('pageshow')); }
    };
}

test('a second submit is cancelled before the deferred loading state runs', () => {
    const page = loadingForm();
    assert.equal(page.submit().defaultPrevented, false);
    assert.equal(page.buttons[0].disabled, false, 'the submitter must still be serialized');
    assert.equal(page.submit().defaultPrevented, true);
});

test('a different submit button cannot send the same form again', () => {
    const page = loadingForm();
    page.submit();
    page.flush();
    assert.equal(page.submit(1).defaultPrevented, true);
});

test('a later validation handler can cancel without locking the form', () => {
    const page = loadingForm();
    const cancel = event => event.preventDefault();
    page.form.addEventListener('submit', cancel);
    assert.equal(page.submit().defaultPrevented, true);
    page.flush();
    assert.equal(page.buttons[0].disabled, false);
    page.form.removeEventListener('submit', cancel);
    assert.equal(page.submit().defaultPrevented, false);
    page.flush();
    assert.equal(page.buttons[0].disabled, true);
});

test('a cancelled event does not block an immediate corrected submission', () => {
    const page = loadingForm();
    page.form.addEventListener('submit', event => event.preventDefault(), { once: true });
    page.submit();
    const retry = page.submit();
    assert.equal(retry.defaultPrevented, false);
    page.flush();
    assert.equal(page.buttons[0].disabled, true);
});

test('Back navigation restores the button and allows a new submission', () => {
    const page = loadingForm();
    page.submit();
    page.flush();
    assert.equal(page.buttons[0].disabled, true);
    page.restore();
    assert.equal(page.buttons[0].disabled, false);
    assert.equal(page.buttons[0].innerHTML, 'Lưu');
    assert.equal(page.submit().defaultPrevented, false);
    page.flush();
    assert.equal(page.buttons[0].disabled, true);
});
