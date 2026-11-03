import io, json, os, sys, unittest
from unittest.mock import patch
from urllib.error import HTTPError
sys.path.insert(0, os.path.join(os.path.dirname(__file__), '..', 'service'))
import server as m

CTX = {'selected': [{'handle': 'A1'}], 'layers': [{'name': '0'}], 'blocks': []}
GOOD = json.dumps({'reply': 'ok', 'actions': [{'type': 'move', 'handles': ['A1'], 'dx': 1}]})


def env(**kw):
    base = {'CAD_AI_PROVIDER': '', 'CAD_AI_BASE_URL': '', 'CAD_AI_API_KEY': '', 'CAD_AI_MODEL': '',
            'OPENAI_API_KEY': '', 'OPENAI_BASE_URL': '', 'OPENAI_MODEL': ''}
    base.update(kw)
    return patch.dict('os.environ', base)


class FakeResp(io.BytesIO):
    def __enter__(self):
        return self

    def __exit__(self, *a):
        self.close()


def http_error(code, body):
    return HTTPError('http://x', code, 'err', {}, io.BytesIO(json.dumps(body).encode()))


class ProviderTests(unittest.TestCase):
    def setUp(self):
        m._history.clear()

    def test_defaults_openai(self):
        with env():
            self.assertEqual(m.provider(), 'openai-compatible')
            self.assertEqual(m.base_url(), 'https://api.openai.com/v1')
            self.assertTrue(m.needs_key())

    def test_ollama_needs_no_key(self):
        with env(CAD_AI_BASE_URL='http://localhost:11434/v1/'):
            self.assertFalse(m.needs_key())
            self.assertEqual(m.base_url(), 'http://localhost:11434/v1')
            self.assertEqual(m.provider_label(), 'Ollama')

    def test_cli_needs_no_key(self):
        with env(CAD_AI_PROVIDER='codex-cli'):
            self.assertFalse(m.needs_key())
        with env(CAD_AI_PROVIDER='bogus'):
            self.assertEqual(m.provider(), 'openai-compatible')

    def test_gemini_request_shape(self):
        seen = {}

        def fake(req, timeout=0):
            seen['url'] = req.full_url
            seen['auth'] = req.get_header('Authorization')
            seen['body'] = json.loads(req.data)
            return FakeResp(json.dumps({'choices': [{'message': {'content': GOOD}}]}).encode())
        with env(CAD_AI_BASE_URL='https://generativelanguage.googleapis.com/v1beta/openai/', CAD_AI_API_KEY='g',
                 CAD_AI_MODEL='gemini-2.5-flash'), patch.object(m, 'urlopen', side_effect=fake):
            r = m.plan('x', CTX)
        self.assertEqual(r['actions'][0]['handles'], ['A1'])
        self.assertEqual(seen['url'], 'https://generativelanguage.googleapis.com/v1beta/openai/chat/completions')
        self.assertEqual(seen['auth'], 'Bearer g')
        self.assertEqual(seen['body']['model'], 'gemini-2.5-flash')

    def test_response_format_fallback(self):
        calls = []

        def fake(req, timeout=0):
            body = json.loads(req.data)
            calls.append(body)
            if 'response_format' in body:
                raise http_error(400, {'error': {'message': 'response_format not supported'}})
            return FakeResp(json.dumps({'choices': [{'message': {'content': GOOD}}]}).encode())
        with env(CAD_AI_API_KEY='k'), patch.object(m, 'urlopen', side_effect=fake):
            self.assertEqual(len(m.plan('x', CTX)['actions']), 1)
        self.assertNotIn('response_format', calls[-1])

    def test_anthropic(self):
        seen = {}

        def fake(req, timeout=0):
            seen['url'] = req.full_url
            seen['key'] = req.get_header('X-api-key')
            seen['body'] = json.loads(req.data)
            return FakeResp(json.dumps({'content': [{'type': 'text', 'text': GOOD}]}).encode())
        with env(CAD_AI_PROVIDER='anthropic', CAD_AI_API_KEY='a'), patch.object(m, 'urlopen', side_effect=fake):
            self.assertEqual(len(m.plan('x', CTX)['actions']), 1)
        self.assertTrue(seen['url'].endswith('/v1/messages'))
        self.assertEqual(seen['key'], 'a')
        self.assertNotIn('system', [x['role'] for x in seen['body']['messages']])

    def test_quota_vs_rate_limit_vs_auth(self):
        quota = json.dumps({'error': {'code': 'insufficient_quota', 'message': 'You exceeded your current quota'}})
        rate = json.dumps({'error': {'code': 'rate_limit_exceeded', 'message': 'Rate limit reached'}})
        self.assertIn('kota', m._friendly_http_error(429, quota, 'OpenAI'))
        self.assertIn('istek sınırı', m._friendly_http_error(429, rate, 'OpenAI'))
        self.assertIn('anahtarı geçersiz', m._friendly_http_error(401, '{}', 'Groq'))
        self.assertTrue(m.is_quota_error(402, '{}'))
        self.assertFalse(m.is_quota_error(429, rate))

    def test_quota_not_retried(self):
        n = []

        def fake(req, timeout=0):
            n.append(1)
            raise http_error(429, {'error': {'code': 'insufficient_quota', 'message': 'quota'}})
        with env(CAD_AI_API_KEY='k'), patch.object(m, 'urlopen', side_effect=fake):
            with self.assertRaises(RuntimeError) as cm:
                m.plan('x', CTX)
        self.assertEqual(len(n), 1)
        self.assertIn('kota', str(cm.exception))

    def test_extract_json(self):
        self.assertEqual(m.extract_json('```json\n{"a": 1}\n```'), {'a': 1})
        self.assertEqual(m.extract_json('Tamam: {"a": 2} bitti'), {'a': 2})
        with self.assertRaises(ValueError):
            m.extract_json('yok')

    def test_codex_cli_args(self):
        captured = {}

        def fake_run(args, stdin_text):
            captured['args'] = args
            captured['stdin'] = stdin_text
            out = args[args.index('-o') + 1]
            with open(out, 'w', encoding='utf-8') as f:
                f.write(GOOD)
            return ''
        with env(CAD_AI_PROVIDER='codex-cli'), patch.object(m, 'find_cli', return_value='codex'), \
                patch.object(m, '_run_cli', side_effect=fake_run):
            self.assertEqual(len(m.plan('x', CTX)['actions']), 1)
        self.assertIn('read-only', captured['args'])
        self.assertEqual(captured['args'][-1], '-')
        self.assertIn('### SYSTEM', captured['stdin'])

    def test_cli_missing(self):
        with env(CAD_AI_PROVIDER='cursor-cli'), patch.object(m, 'find_cli', return_value=None):
            with self.assertRaises(RuntimeError):
                m.plan('x', CTX)

    def test_question_answered_without_selection(self):
        ctx = dict(CTX, selected=[], drawing_summary={'block_references_total_by_name': {'blok': 7}})
        ans = json.dumps({'reply': 'Çizimde 7 adet blok var.', 'actions': []})
        with env(CAD_AI_API_KEY='k'), patch.object(m, 'call_llm', return_value=ans) as c:
            r = m.plan('blok isimli blok kaç adet var', ctx)
        self.assertEqual(r['actions'], [])
        self.assertIn('7', r['reply'])
        sent = json.loads(c.call_args[0][0][-1]['content'])
        self.assertEqual(sent['drawing_context']['drawing_summary']['block_references_total_by_name']['blok'], 7)
        self.assertIn('drawing_summary', m.SYSTEM)
        self.assertIn('QUESTIONS', m.SYSTEM)

    def test_mask(self):
        self.assertNotIn('AIzaSyABCDEFGHIJKLMNOP', m._mask('key AIzaSyABCDEFGHIJKLMNOP'))


if __name__ == '__main__':
    unittest.main()