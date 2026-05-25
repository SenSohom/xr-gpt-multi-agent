import logging
import os
import time
from dataclasses import dataclass
from typing import Callable, Dict, List, Optional, Tuple

from PIL import Image

log = logging.getLogger("vrxr.vlm_agents")


@dataclass(frozen=True)
class AgentSpec:
    name: str
    role_prompt: str
    max_new_tokens: int = 96


@dataclass
class AgentResult:
    agent: str
    answer: str
    latency_ms: float
    error: str = ""


AGENTS: Dict[str, AgentSpec] = {
    "describe": AgentSpec(
        name="describe",
        role_prompt=(
            "You are the object description agent. Identify the selected object "
            "from the image and answer in one concise sentence."
        ),
        max_new_tokens=64,
    ),
    "detail": AgentSpec(
        name="detail",
        role_prompt=(
            "You are the detail agent. Explain what the selected object is and "
            "what it is typically used for in 2-3 practical sentences."
        ),
        max_new_tokens=128,
    ),
    "usage": AgentSpec(
        name="usage",
        role_prompt=(
            "You are the usage agent. Give clear practical advice for using the "
            "selected object. Keep the answer brief and actionable."
        ),
        max_new_tokens=128,
    ),
    "mechanism": AgentSpec(
        name="mechanism",
        role_prompt=(
            "You are the mechanism agent. Explain simply how the selected object "
            "works or why it functions the way it does."
        ),
        max_new_tokens=96,
    ),
    "safety": AgentSpec(
        name="safety",
        role_prompt=(
            "You are the safety agent. Look for obvious handling risks, hazards, "
            "or precautions for the selected object. If none are visible, say so."
        ),
        max_new_tokens=96,
    ),
    "compare": AgentSpec(
        name="compare",
        role_prompt=(
            "You are the comparison agent. Compare the selected object with one "
            "similar item or share one useful distinguishing fact."
        ),
        max_new_tokens=96,
    ),
    "chat": AgentSpec(
        name="chat",
        role_prompt=(
            "You are the conversational visual assistant. Answer the user's "
            "question using the image and the detected label as context."
        ),
        max_new_tokens=160,
    ),
    "critic": AgentSpec(
        name="critic",
        role_prompt=(
            "You are the critic agent. Review the previous answer for obvious "
            "visual contradictions, overclaiming, or missing safety caveats. "
            "Return a corrected final answer, not commentary about the review."
        ),
        max_new_tokens=128,
    ),
}


# Agent communication matrix. Values are downstream agents that may be called
# after the key agent has produced its result.
COMMUNICATION_MATRIX: Dict[str, List[str]] = {
    "describe": [],
    "detail": ["critic"],
    "usage": ["critic"],
    "mechanism": ["critic"],
    "safety": ["critic"],
    "compare": ["critic"],
    "chat": ["critic"],
    "critic": [],
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


class MoondreamEngine(VlmEngine):
    model_id = "vikhyatk/moondream2"

    def __init__(self, torch_module, device: str):
        self.torch = torch_module
        self.device = device
        from transformers import AutoModelForCausalLM, AutoTokenizer

        log.info("Loading moondream2 model (%s) ...", self.model_id)
        self.tokenizer = AutoTokenizer.from_pretrained(
            self.model_id,
            trust_remote_code=True,
        )
        self.model = AutoModelForCausalLM.from_pretrained(
            self.model_id,
            trust_remote_code=True,
            torch_dtype=(
                torch_module.float16 if device == "cuda" else torch_module.float32
            ),
        ).to(device)
        self.model.eval()
        log.info("moondream2 loaded.")

    def answer(self, image: Image.Image, prompt: str, max_new_tokens: int) -> str:
        del max_new_tokens
        with self.torch.no_grad():
            enc = self.model.encode_image(image)
            ans = self.model.answer_question(enc, prompt, self.tokenizer)
        return ans.strip()


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
        sequence = [primary_agent]
        if enable_critic:
            sequence.extend(COMMUNICATION_MATRIX.get(primary_agent, []))

        results: List[AgentResult] = []
        current_prompt = self._build_agent_prompt(
            AGENTS[primary_agent],
            label,
            prompt,
            previous_answer="",
        )

        answer = ""
        for agent_name in sequence:
            spec = AGENTS[agent_name]
            if agent_name == "critic":
                current_prompt = self._build_agent_prompt(
                    spec,
                    label,
                    prompt,
                    previous_answer=answer,
                )

            t0 = time.perf_counter()
            try:
                raw_answer = self.engine_factory().answer(
                    image,
                    current_prompt,
                    min(max_new_tokens, spec.max_new_tokens),
                )
                latency_ms = (time.perf_counter() - t0) * 1000.0
                answer = raw_answer.strip()
                results.append(AgentResult(agent_name, answer, latency_ms))
            except Exception as e:
                latency_ms = (time.perf_counter() - t0) * 1000.0
                log.exception("VLM agent '%s' failed", agent_name)
                results.append(AgentResult(agent_name, "", latency_ms, str(e)))
                raise

        return answer, results

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
    ) -> str:
        parts = [spec.role_prompt]
        if label:
            parts.append(f"Detected label hint: {label}.")
        if previous_answer:
            parts.append(f"Previous agent answer: {previous_answer}")
        parts.append(f"User request: {user_prompt}")
        return "\n".join(parts)


def desired_backend() -> str:
    return os.environ.get("VRXR_VLM_BACKEND", "fastvlm").strip().lower()

