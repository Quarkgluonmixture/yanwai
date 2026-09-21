"""Unified extraction inside an isolated bubble; no message detection or trust policy."""
import math
import unicodedata


def is_cjk(char):
    return '\u3400' <= char <= '\u4dbf' or '\u4e00' <= char <= '\u9fff' or '\uf900' <= char <= '\ufaff'


def is_cjk_punctuation(char):
    return ('\u3000' <= char <= '\u303f' or '\uff01' <= char <= '\uff65') and unicodedata.category(char).startswith('P')


def compose(lines):
    text = ''
    for line in lines:
        if not line:
            continue
        separator = '' if not text or text[-1].isspace() or line[0].isspace() else ' '
        if text and (is_cjk(text[-1]) or is_cjk(line[0]) or is_cjk_punctuation(text[-1]) or is_cjk_punctuation(line[0])):
            separator = ''
        text += separator + line
    return text


def boxes_from_polygons(polygons, width, height):
    boxes = []
    for polygon in polygons:
        xs, ys = zip(*polygon)
        box = [max(0, math.floor(min(xs))), max(0, math.floor(min(ys))),
               min(width, math.ceil(max(xs))), min(height, math.ceil(max(ys)))]
        if box[2] <= box[0] or box[3] <= box[1]:
            raise ValueError('Invalid detected line box')
        boxes.append(box)
    return sorted(boxes, key=lambda box: ((box[1]+box[3])/2, box[0]))
