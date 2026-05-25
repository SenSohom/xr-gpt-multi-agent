import logging
import re
import time
from dataclasses import dataclass
from typing import Callable, Dict, List, Tuple

from PIL import Image

log = logging.getLogger("vrxr.vlm_agents")


@dataclass(frozen=True)
class AgentSpec:
    name: str
    role_prompt: str
    max_new_tokens: int = 96
    max_sentences: int = 1
    max_words: int = 28


@dataclass
class AgentResult:
    agent: str
    stage: str
    answer: str
    latency_ms: float
    error: str = ""


AGENTS: Dict[str, AgentSpec] = {
    "describe": AgentSpec(
        name="describe",
        role_prompt=(
            "You are the object label agent for a mixed-reality HUD. Return only "
            "the object name or one short noun phrase. No explanation. No prefix."
        ),
        max_new_tokens=24,
        max_sentences=1,
        max_words=8,
    ),
    "detail": AgentSpec(
        name="detail",
        role_prompt=(
            "You are the detail agent. Explain what the selected object is and "
            "what it is typically used for. Answer in at most two short sentences."
        ),
        max_new_tokens=72,
        max_sentences=2,
        max_words=48,
    ),
    "usage": AgentSpec(
        name="usage",
        role_prompt=(
            "You are the usage agent. Give clear practical advice for using the "
            "selected object. Answer with one short actionable sentence."
        ),
        max_new_tokens=56,
        max_sentences=1,
        max_words=28,
    ),
    "mechanism": AgentSpec(
        name="mechanism",
        role_prompt=(
            "You are the mechanism agent. Explain simply how the selected object "
            "works or why it functions the way it does. Use one short sentence."
        ),
        max_new_tokens=56,
        max_sentences=1,
        max_words=30,
    ),
    "safety": AgentSpec(
        name="safety",
        role_prompt=(
            "You are the safety agent. Look for obvious handling risks, hazards, "
            "or precautions for the selected object. If the risk is trivial or "
            "none is visible, answer exactly: No obvious safety concern."
        ),
        max_new_tokens=48,
        max_sentences=1,
        max_words=24,
    ),
    "compare": AgentSpec(
        name="compare",
        role_prompt=(
            "You are the comparison agent. Compare the selected object with one "
            "similar item or share one useful distinguishing fact. Use one sentence."
        ),
        max_new_tokens=56,
        max_sentences=1,
        max_words=32,
    ),
    "chat": AgentSpec(
        name="chat",
        role_prompt=(
            "You are the conversational visual assistant. Answer the user's "
            "question using the image and the detected label as context. Keep it "
            "brief enough for a headset UI."
        ),
        max_new_tokens=96,
        max_sentences=2,
        max_words=60,
    ),
    "critic": AgentSpec(
        name="critic",
        role_prompt=(
            "You are the critic agent. Review the previous answer for obvious "
            "visual contradictions, overclaiming, or missing safety caveats. "
            "Return only the corrected final answer. Maximum one short sentence."
        ),
        max_new_tokens=48,
        max_sentences=1,
        max_words=28,
    ),
    "synthesizer": AgentSpec(
        name="synthesizer",
        role_prompt=(
            "You are the synthesis agent for a mixed-reality HUD. Merge the "
            "agent observations into one final answer that directly satisfies "
            "the user's request. Keep it concise and do not mention agents. "
            "Do not invent warnings, labels, text, or features that are not "
            "clearly supported by the agent observations. Include safety only "
            "when the user asked for safety or a concrete visible hazard was found."
        ),
        max_new_tokens=72,
        max_sentences=2,
        max_words=48,
    ),
}


# Agent communication matrix. Values are peer agents that should be consulted
# after the primary agent has produced its first-pass answer.
COMMUNICATION_MATRIX: Dict[str, List[str]] = {
    "describe": [],
    "detail": ["describe", "usage"],
    "usage": ["describe", "safety"],
    "mechanism": ["describe"],
    "safety": [],
    "compare": ["describe", "detail"],
    "chat": ["describe", "usage"],
    "critic": [],
    "synthesizer": [],
}


TASK_TO_AGENT = {
    "describe": "describe",
    "more_info": "detail",
    "detail": "detail",
    "intro": "describe",
    "use": "usage",
    "usage": "usage",
    "why": "mechanism",
    "mechanism": "mechanism",
    "next": "safety",
    "safety": "safety",
    "compare": "compare",
    "fun_fact": "compare",
    "chat": "chat",
}


class VlmEngine:
    def answer(self, image: Image.Image, prompt: str, max_new_tokens: int) -> str:
        raise NotImplementedError


class FastVlmEngine(VlmEngine):
    model_id = "apple/FastVLM-0.5B"
    image_token_index = -200

    def __init__(self, torch_module):
        self.torch = torch_module
        from transformers import AutoModelForCausalLM, AutoTokenizer

        log.info("Loading FastVLM model (%s) ...", self.model_id)
        self.tokenizer = AutoTokenizer.from_pretrained(
            self.model_id,
            trust_remote_code=True,
        )
        self.model = AutoModelForCausalLM.from_pretrained(
            self.model_id,
            torch_dtype=(
                torch_module.float16
                if torch_module.cuda.is_available()
                else torch_module.float32
            ),
            device_map="auto",
            trust_remote_code=True,
        )
        self.model.eval()
        log.info("FastVLM loaded.")

    def answer(self, image: Image.Image, prompt: str, max_new_tokens: int) -> str:
        messages = [{"role": "user", "content": f"<image>\n{prompt}"}]
        rendered = self.tokenizer.apply_chat_template(
            messages,
            add_generation_prompt=True,
            tokenize=False,
        )
        pre, post = rendered.split("<image>", 1)
        pre_ids = self.tokenizer(
            pre,
            return_tensors="pt",
            add_special_tokens=False,
        ).input_ids
        post_ids = self.tokenizer(
            post,
            return_tensors="pt",
            add_special_tokens=False,
        ).input_ids
        img_tok = self.torch.tensor([[self.image_token_index]], dtype=pre_ids.dtype)
        input_ids = self.torch.cat([pre_ids, img_tok, post_ids], dim=1).to(
            self.model.device
        )
        attention_mask = self.torch.ones_like(input_ids, device=self.model.device)

        pixel_values = self.model.get_vision_tower().image_processor(
            images=image,
            return_tensors="pt",
        )["pixel_values"]
        pixel_values = pixel_values.to(self.model.device, dtype=self.model.dtype)

        with self.torch.no_grad():
            out = self.model.generate(
                inputs=input_ids,
                attention_mask=attention_mask,
                images=pixel_values,
                max_new_tokens=max_new_tokens,
            )

        decoded = self.tokenizer.decode(out[0], skip_special_tokens=True)
        return self._strip_prompt_echo(decoded, prompt)

    @staticmethod
    def _strip_prompt_echo(decoded: str, prompt: str) -> str:
        text = decoded.strip()
        if prompt in text:
            text = text.split(prompt, 1)[-1].strip()
        for marker in ("assistant", "ASSISTANT:", "Assistant:"):
            if marker in text:
                text = text.split(marker)[-1].strip()
        return text.strip()


class AgentRouter:
    def __init__(self, engine_factory: Callable[[], VlmEngine]):
        self.engine_factory = engine_factory

    def ask(
        self,
        image: Image.Image,
        label: str,
        prompt: str,
        task: str = "",
        max_new_tokens: int = 96,
        enable_critic: bool = False,
    ) -> Tuple[str, List[AgentResult]]:
        primary_agent = self._select_agent(task, prompt)
        results: List[AgentResult] = []

        primary_answer = self._run_agent(
            agent_name=primary_agent,
            stage="primary",
            image=image,
            label=label,
            user_prompt=prompt,
            max_new_tokens=max_new_tokens,
            results=results,
        )

        peer_answers: List[AgentResult] = []
        for peer_agent in self._peer_agents_for(primary_agent):
            peer_answer = self._run_agent(
                agent_name=peer_agent,
                stage="peer",
                image=image,
                label=label,
                user_prompt=prompt,
                max_new_tokens=max_new_tokens,
                results=results,
                previous_answer=primary_answer,
            )
            peer_answers.append(results[-1])

        answer = primary_answer
        if peer_answers:
            answer = self._run_agent(
                agent_name="synthesizer",
                stage="synthesis",
                image=image,
                label=label,
                user_prompt=prompt,
                max_new_tokens=max_new_tokens,
                results=results,
                previous_answer=primary_answer,
                peer_answers=[results[0], *peer_answers],
            )

        if enable_critic:
            answer = self._run_agent(
                agent_name="critic",
                stage="critic",
                image=image,
                label=label,
                user_prompt=prompt,
                max_new_tokens=max_new_tokens,
                results=results,
                previous_answer=answer,
                peer_answers=results,
            )

        return answer, results

    def _run_agent(
        self,
        agent_name: str,
        stage: str,
        image: Image.Image,
        label: str,
        user_prompt: str,
        max_new_tokens: int,
        results: List[AgentResult],
        previous_answer: str = "",
        peer_answers: List[AgentResult] | None = None,
    ) -> str:
        spec = AGENTS[agent_name]
        current_prompt = self._build_agent_prompt(
            spec,
            label,
            user_prompt,
            previous_answer=previous_answer,
            peer_answers=peer_answers,
        )

        t0 = time.perf_counter()
        try:
            raw_answer = self.engine_factory().answer(
                image,
                current_prompt,
                min(max_new_tokens, spec.max_new_tokens),
            )
            latency_ms = (time.perf_counter() - t0) * 1000.0
            answer = self._sanitize_answer(raw_answer, spec)
            results.append(AgentResult(agent_name, stage, answer, latency_ms))
            return answer
        except Exception as e:
            latency_ms = (time.perf_counter() - t0) * 1000.0
            log.exception("VLM agent '%s' failed", agent_name)
            results.append(AgentResult(agent_name, stage, "", latency_ms, str(e)))
            raise

    @staticmethod
    def _peer_agents_for(primary_agent: str) -> List[str]:
        seen = {primary_agent}
        peers: List[str] = []
        for agent_name in COMMUNICATION_MATRIX.get(primary_agent, []):
            if agent_name in seen:
                continue
            if agent_name not in AGENTS:
                log.warning("Unknown agent '%s' in communication matrix", agent_name)
                continue
            peers.append(agent_name)
            seen.add(agent_name)
        return peers

    @staticmethod
    def _select_agent(task: str, prompt: str) -> str:
        normalized = (task or "").strip().lower()
        if normalized in TASK_TO_AGENT:
            return TASK_TO_AGENT[normalized]

        p = (prompt or "").lower()
        if "safety" in p or "warning" in p or "precaution" in p:
            return "safety"
        if "use" in p or "steps" in p or "tips" in p:
            return "usage"
        if "works" in p or "function" in p or "why" in p:
            return "mechanism"
        if "compare" in p or "similar" in p or "fact" in p:
            return "compare"
        if "2-3" in p or "more" in p or "typically used" in p:
            return "detail"
        if "short sentence" in p or "identify and describe" in p:
            return "describe"
        return "chat"

    @staticmethod
    def _build_agent_prompt(
        spec: AgentSpec,
        label: str,
        user_prompt: str,
        previous_answer: str,
        peer_answers: List[AgentResult] | None = None,
    ) -> str:
        parts = [spec.role_prompt]
        if label:
            parts.append(f"Detected label hint: {label}.")
        if previous_answer:
            parts.append(f"Previous agent answer: {previous_answer}")
        if peer_answers:
            parts.append("Agent observations:")
            for result in peer_answers:
                if result.answer:
                    parts.append(f"- {result.agent}: {result.answer}")
        parts.append(f"User request: {user_prompt}")
        parts.append(
            f"Output limit: {spec.max_sentences} sentence(s), {spec.max_words} words max."
        )
        return "\n".join(parts)

    @staticmethod
    def _sanitize_answer(text: str, spec: AgentSpec) -> str:
        if not text:
            return ""

        cleaned = text.strip()
        cleaned = cleaned.replace("\r", "\n")
        cleaned = re.sub(r"\n+", "\n", cleaned)

        # Keep the first useful line when the model starts a useful answer and
        # then rambles. FastVLM can echo parts of the prompt, so drop those.
        prompt_echo_prefixes = (
            "you are ",
            "detected label hint:",
            "previous agent answer:",
            "user request:",
            "output limit:",
            "return only",
            "no explanation",
            "no prefix",
        )
        lines = []
        for line in cleaned.split("\n"):
            line = line.strip(" -\t")
            if not line:
                continue
            lowered_line = line.lower()
            if any(lowered_line.startswith(prefix) for prefix in prompt_echo_prefixes):
                continue
            lines.append(line)
        if lines:
            cleaned = lines[0]

        prefixes = (
            "answer:",
            "final answer:",
            "object:",
            "description:",
            "the answer is",
        )
        lowered = cleaned.lower()
        for prefix in prefixes:
            if lowered.startswith(prefix):
                cleaned = cleaned[len(prefix):].strip(" :.-")
                lowered = cleaned.lower()
                break

        # Remove common refusal/meta tails that FastVLM can append after answering.
        tail_markers = (
            "i have examined",
            "i encountered",
            "i'm sorry",
            "i am unable",
            "since the question",
            "this process involves",
        )
        lowered = cleaned.lower()
        cut_points = [lowered.find(marker) for marker in tail_markers if lowered.find(marker) > 0]
        if cut_points:
            cleaned = cleaned[:min(cut_points)].strip(" .,\n")

        sentences = re.split(r"(?<=[.!?])\s+", cleaned)
        if spec.max_sentences > 0 and len(sentences) > spec.max_sentences:
            cleaned = " ".join(sentences[: spec.max_sentences]).strip()

        words = cleaned.split()
        if spec.max_words > 0 and len(words) > spec.max_words:
            cleaned = " ".join(words[: spec.max_words]).rstrip(" ,;:")
            if spec.max_sentences != 0 and not cleaned.endswith((".", "!", "?")):
                cleaned += "."

        return cleaned.strip()
