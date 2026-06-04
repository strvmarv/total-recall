# hermes-plugin/__init__.py
"""total-recall memory provider for Hermes Agent."""


def register(ctx):
    """Register the total-recall memory provider with Hermes."""
    from .provider import TotalRecallProvider
    provider = TotalRecallProvider()
    ctx.register_memory_provider(provider)
