using System.Text.Json;

namespace Starlight.Tools.Web;

/// <summary>Scripts that run in the login page. Values go in as JSON strings, so nothing in them can break out.</summary>
internal static class LoginScripts
{
    private const string Helpers = """
        const vis = e => !!(e && (e.offsetWidth || e.offsetHeight || e.getClientRects().length));
        const set = (el, v) => {
          el.focus();
          const d = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value');
          d.set.call(el, v);
          el.dispatchEvent(new Event('input', { bubbles: true }));
          el.dispatchEvent(new Event('change', { bubbles: true }));
        };
        const submit = el => {
          const f = el.form;
          const b = [...(f || document).querySelectorAll('button[type=submit],input[type=submit],button:not([type])')].find(vis);
          if (b) b.click(); else if (f) { f.requestSubmit ? f.requestSubmit() : f.submit(); }
          else el.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', code: 'Enter', keyCode: 13, bubbles: true }));
        };
        const codeField = () => [...document.querySelectorAll('input')].filter(vis).find(i =>
          i.autocomplete === 'one-time-code' || /otp|2fa|mfa|totp|one.?time|verification|security.?code|auth.?code/i.test(i.name + ' ' + i.id + ' ' + (i.getAttribute('aria-label') || '') + ' ' + (i.placeholder || '')));
        """;

    public static string Fill(string user, string password) => $$"""
        (() => {
          {{Helpers}}
          const pw = [...document.querySelectorAll('input[type=password]')].find(vis);
          const users = [...document.querySelectorAll('input[type=email],input[type=text],input[autocomplete=username],input:not([type])')].filter(vis)
            .filter(i => !pw || (i.compareDocumentPosition(pw) & Node.DOCUMENT_POSITION_FOLLOWING));
          const user = users.find(i => /user|login|mail|account|name|id/i.test(i.name + i.id + i.autocomplete + i.type)) || users[users.length - 1];
          if (user && !user.value) set(user, {{JsonSerializer.Serialize(user)}});
          if (!pw) { if (!user) return 'nofields'; submit(user); return 'user-only'; }
          set(pw, {{JsonSerializer.Serialize(password)}});
          submit(pw);
          return 'submitted';
        })()
        """;

    public const string State = $$"""
        (() => {
          {{Helpers}}
          if ([...document.querySelectorAll('input[type=password]')].some(vis)) return 'password';
          if (codeField()) return 'code';
          return 'ok';
        })()
        """;

    public static string FillCode(string code) => $$"""
        (() => {
          {{Helpers}}
          const f = codeField();
          if (!f) return 'nofield';
          set(f, {{JsonSerializer.Serialize(code)}});
          submit(f);
          return 'submitted';
        })()
        """;
}
